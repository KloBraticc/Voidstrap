using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Voidstrap.Utility;

public static class EmulationBypassService
{
	private const int ProcessPowerThrottling = 4;

	private const uint PowerThrottlingCurrentVersion = 1;

	private const uint ExecutionSpeed = 0x1;

	private const uint IgnoreTimerResolution = 0x4;

	private const int ErrorInvalidParameter = 87;

	private const string LayersKeyPath = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers";

	private const string BackupKeyPath = "Software\\Voidstrap\\EmulationBypass";

	private const string ImageOptionsKeyPath = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";

	private static readonly string[] EmulationLayers =
	{
		"256COLOR",
		"640X480",
		"8BITCOLOR",
		"16BITCOLOR",
		"DISABLEDWM",
		"DISABLETHEMES",
		"DPIUNAWARE",
		"GDIDPISCALING"
	};

	private static readonly string[] EmulationLayerPrefixes = { "WIN", "VISTA", "NT4" };

	private static readonly string[] ImageOptionValues =
	{
		"Debugger",
		"GlobalFlag",
		"MitigationOptions",
		"MitigationAuditOptions",
		"TracingFlags",
		"PageHeapFlags",
		"VerifierDlls"
	};

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessPowerThrottlingState
	{
		public uint Version;

		public uint ControlMask;

		public uint StateMask;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetProcessInformation(IntPtr hProcess, int informationClass, ref ProcessPowerThrottlingState information, uint size);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWow64Process2(IntPtr hProcess, out ushort processMachine, out ushort nativeMachine);

	public static void ApplyBypassEnvironment(ProcessStartInfo startInfo)
	{
		if (startInfo == null)
			return;

		if (startInfo.UseShellExecute)
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyBypassEnvironment", "Skipped the environment bypass because this launch needs ShellExecute, which cannot carry environment variables");
			return;
		}

		try
		{
			startInfo.EnvironmentVariables.Remove("__COMPAT_LAYER");
			startInfo.EnvironmentVariables["_NO_DEBUG_HEAP"] = "1";
			App.Logger.WriteLine("EmulationBypassService::ApplyBypassEnvironment", "Cleared the inherited compatibility layer variable and disabled the NT debug heap for this launch");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyBypassEnvironment", "Failed to apply environment bypass: " + ex.Message);
		}
	}

	public static void RemoveBypassEnvironment(ProcessStartInfo startInfo)
	{
		if (startInfo == null)
			return;

		if (startInfo.UseShellExecute)
			return;

		try
		{
			startInfo.EnvironmentVariables.Remove("__COMPAT_LAYER");
			startInfo.EnvironmentVariables.Remove("_NO_DEBUG_HEAP");
			startInfo.EnvironmentVariables.Remove("DISABLE_LAYER");
			startInfo.EnvironmentVariables.Remove("__COMPAT_LAYER_NO_APPHACKS");
			startInfo.EnvironmentVariables.Remove("AMD_DISABLE_AGS_BUILD");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::RemoveBypassEnvironment", "Failed to remove environment bypass: " + ex.Message);
		}
	}

	public static void ApplyCompatLayerBypass(string executablePath)
	{
		if (!Platform.SupportsRegistry || string.IsNullOrWhiteSpace(executablePath))
			return;

		try
		{
			using RegistryKey? backup = Registry.CurrentUser.CreateSubKey(BackupKeyPath);
			using RegistryKey? layers = Registry.CurrentUser.CreateSubKey(LayersKeyPath);

			if (backup == null || layers == null)
				return;

			PruneStaleBackups(backup, layers, executablePath);

			string current = layers.GetValue(executablePath) as string ?? string.Empty;
			string stripped = StripEmulationLayers(current, out List<string> removed);

			if (removed.Count == 0)
			{
				App.Logger.WriteLine("EmulationBypassService::ApplyCompatLayerBypass", "No Windows compatibility layers are attached to this Roblox build");
			}
			else
			{
				if (backup.GetValue(executablePath) == null)
					backup.SetValue(executablePath, current);

				if (string.IsNullOrEmpty(stripped))
					layers.DeleteValue(executablePath, throwOnMissingValue: false);
				else
					layers.SetValue(executablePath, stripped);

				App.Logger.WriteLine("EmulationBypassService::ApplyCompatLayerBypass", "Detached the Windows compatibility engine from this Roblox build by removing: " + string.Join(", ", removed));
			}

			ReportMachineWideLayers(executablePath);
			ReportImageFileExecutionOptions(executablePath);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyCompatLayerBypass", "Failed to strip the compatibility layers: " + ex.Message);
		}
	}

	public static void RestoreCompatLayers()
	{
		if (!Platform.SupportsRegistry)
			return;

		try
		{
			using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupKeyPath, writable: true);

			if (backup == null)
				return;

			using RegistryKey? layers = Registry.CurrentUser.CreateSubKey(LayersKeyPath);

			if (layers == null)
				return;

			foreach (string name in backup.GetValueNames())
				RestoreOne(backup, layers, name);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::RestoreCompatLayers", "Failed to restore the original compatibility layers: " + ex.Message);
		}
	}

	private static void PruneStaleBackups(RegistryKey backup, RegistryKey layers, string executablePath)
	{
		foreach (string name in backup.GetValueNames())
		{
			if (string.Equals(name, executablePath, StringComparison.OrdinalIgnoreCase))
				continue;

			if (File.Exists(name))
			{
				RestoreOne(backup, layers, name);
				continue;
			}

			try
			{
				layers.DeleteValue(name, throwOnMissingValue: false);
				backup.DeleteValue(name, throwOnMissingValue: false);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("EmulationBypassService::PruneStaleBackups", "Could not clear the entry for a removed Roblox build: " + ex.Message);
			}
		}
	}

	private static void RestoreOne(RegistryKey backup, RegistryKey layers, string name)
	{
		try
		{
			string original = backup.GetValue(name) as string ?? string.Empty;

			if (string.IsNullOrEmpty(original))
				layers.DeleteValue(name, throwOnMissingValue: false);
			else
				layers.SetValue(name, original);

			backup.DeleteValue(name, throwOnMissingValue: false);
			App.Logger.WriteLine("EmulationBypassService::RestoreOne", "Restored the original Windows compatibility layers for " + name);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::RestoreOne", "Could not restore the compatibility layers for " + name + ": " + ex.Message);
		}
	}

	private static string StripEmulationLayers(string value, out List<string> removed)
	{
		removed = new List<string>();

		if (string.IsNullOrWhiteSpace(value))
			return string.Empty;

		List<string> kept = new List<string>();

		foreach (string token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (IsEmulationLayer(token))
				removed.Add(token);
			else
				kept.Add(token);
		}

		if (removed.Count == 0)
			return value;

		foreach (string token in kept)
		{
			if (!IsControlToken(token))
				return string.Join(" ", kept);
		}

		return string.Empty;
	}

	private static bool IsEmulationLayer(string token)
	{
		if (IsControlToken(token))
			return false;

		foreach (string layer in EmulationLayers)
		{
			if (token.Equals(layer, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		foreach (string prefix in EmulationLayerPrefixes)
		{
			if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	private static bool IsControlToken(string token)
	{
		if (string.IsNullOrEmpty(token))
			return true;

		char first = token[0];
		return first == '~' || first == '$' || first == '^' || first == '#';
	}

	private static void ReportMachineWideLayers(string executablePath)
	{
		try
		{
			using RegistryKey? layers = Registry.LocalMachine.OpenSubKey(LayersKeyPath);

			if (layers?.GetValue(executablePath) is not string value || string.IsNullOrWhiteSpace(value))
				return;

			StripEmulationLayers(value, out List<string> removed);

			if (removed.Count > 0)
				App.Logger.WriteLine("EmulationBypassService::ReportMachineWideLayers", "Windows still applies machine wide compatibility layers that only an administrator can clear: " + string.Join(", ", removed));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ReportMachineWideLayers", "Could not read the machine wide compatibility layers: " + ex.Message);
		}
	}

	private static void ReportImageFileExecutionOptions(string executablePath)
	{
		try
		{
			string fileName = Path.GetFileName(executablePath);

			if (string.IsNullOrEmpty(fileName))
				return;

			using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ImageOptionsKeyPath + "\\" + fileName);

			if (key == null)
				return;

			List<string> found = new List<string>();

			foreach (string valueName in ImageOptionValues)
			{
				object? value = key.GetValue(valueName);

				if (value != null)
					found.Add(valueName + " = " + value);
			}

			if (found.Count > 0)
				App.Logger.WriteLine("EmulationBypassService::ReportImageFileExecutionOptions", "Windows has instrumentation attached to " + fileName + " that slows every launch, an administrator has to clear it: " + string.Join(", ", found));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ReportImageFileExecutionOptions", "Could not read the image file execution options: " + ex.Message);
		}
	}

	public static void ApplyProcessBypass(Process process)
	{
		if (process == null)
			return;

		if (!Platform.IsWindows)
			return;

		IntPtr handle;
		try
		{
			if (process.HasExited)
				return;

			handle = process.Handle;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyProcessBypass", "Could not open the process handle: " + ex.Message);
			return;
		}

		if (handle == IntPtr.Zero)
			return;

		ReportProcessEmulation(handle);

		if (TrySetThrottling(handle, ExecutionSpeed | IgnoreTimerResolution, out int error))
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyProcessBypass", "Disabled power throttling for process " + process.Id);
			return;
		}

		if (error == ErrorInvalidParameter && TrySetThrottling(handle, ExecutionSpeed, out error))
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyProcessBypass", "Disabled power throttling for process " + process.Id + " without the timer resolution flag");
			return;
		}

		App.Logger.WriteLine("EmulationBypassService::ApplyProcessBypass", "Windows refused the power throttling change (error " + error + ")");
	}

	private static void ReportProcessEmulation(IntPtr handle)
	{
		try
		{
			if (!IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
				return;

			if (processMachine == 0)
			{
				App.Logger.WriteLine("EmulationBypassService::ReportProcessEmulation", "Roblox is running natively on this CPU, no instruction emulation is in play");
				return;
			}

			App.Logger.WriteLine("EmulationBypassService::ReportProcessEmulation", "Roblox is running under CPU emulation: " + DescribeMachine(processMachine) + " code on a " + DescribeMachine(nativeMachine) + " system, which no launcher can remove");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ReportProcessEmulation", "Could not read the process emulation state: " + ex.Message);
		}
	}

	private static string DescribeMachine(ushort machine)
	{
		return machine switch
		{
			0x014C => "x86",
			0x8664 => "x64",
			0xAA64 => "ARM64",
			0x01C4 => "ARM",
			_ => "0x" + machine.ToString("X4")
		};
	}

	public static void ApplyEfficiencyMode(Process process)
	{
		if (process == null || !Platform.IsWindows)
			return;

		IntPtr handle;
		try
		{
			if (process.HasExited)
				return;

			handle = process.Handle;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyEfficiencyMode", "Could not open the process handle: " + ex.Message);
			return;
		}

		if (handle == IntPtr.Zero)
			return;

		if (TrySetThrottling(handle, ExecutionSpeed, out int error, ExecutionSpeed))
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyEfficiencyMode", "Enabled efficiency mode for process " + process.Id);
		}
		else
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyEfficiencyMode", "Windows refused the efficiency mode change (error " + error + ")");
			return;
		}

		try
		{
			process.PriorityClass = ProcessPriorityClass.Idle;
			App.Logger.WriteLine("EmulationBypassService::ApplyEfficiencyMode", "Lowered priority for process " + process.Id);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyEfficiencyMode", "Could not lower the process priority: " + ex.Message);
		}
	}

	private static bool TrySetThrottling(IntPtr handle, uint controlMask, out int error, uint stateMask = 0)
	{
		error = 0;
		ProcessPowerThrottlingState state = new ProcessPowerThrottlingState
		{
			Version = PowerThrottlingCurrentVersion,
			ControlMask = controlMask,
			StateMask = stateMask
		};

		try
		{
			if (SetProcessInformation(handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
				return true;

			error = Marshal.GetLastWin32Error();
			return false;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("EmulationBypassService::ApplyProcessBypass", "Could not change power throttling: " + ex.Message);
			return false;
		}
	}
}
