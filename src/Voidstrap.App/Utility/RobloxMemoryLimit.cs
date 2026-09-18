using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

internal static partial class RobloxMemoryLimit
{
	public const int MinimumMb = 1024;

	public const int StepMb = 256;

	public const int DefaultMb = 4096;

	private const uint QuotaLimitsHardwsMinDisable = 0x2;
	private const uint QuotaLimitsHardwsMaxEnable = 0x4;
	private const uint QuotaLimitsHardwsMaxDisable = 0x8;
	private const ulong MinimumWorkingSetBytes = 64UL * 1024 * 1024;

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatusEx
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhys;
		public ulong AvailPhys;
		public ulong TotalPageFile;
		public ulong AvailPageFile;
		public ulong TotalVirtual;
		public ulong AvailVirtual;
		public ulong AvailExtendedVirtual;
	}

	[LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

	[LibraryImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSizeEx", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetProcessWorkingSetSizeEx(IntPtr process, nuint minimum, nuint maximum, uint flags);

	[LibraryImport("kernel32.dll", EntryPoint = "GetProcessWorkingSetSizeEx", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetProcessWorkingSetSizeEx(IntPtr process, out nuint minimum, out nuint maximum, out uint flags);

	public static (int TotalMb, int AvailableMb) SystemMemory()
	{
		MemoryStatusEx status = new() { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
		if (!Platform.IsWindows || !GlobalMemoryStatusEx(ref status))
			return (16384, 8192);
		return ((int)(status.TotalPhys / 1048576UL), (int)(status.AvailPhys / 1048576UL));
	}

	public static int MaximumMb => Math.Max(MinimumMb, SystemMemory().TotalMb / StepMb * StepMb);

	public static int Clamp(int mb)
	{
		int snapped = (int)Math.Round(mb / (double)StepMb) * StepMb;
		return Math.Clamp(snapped, MinimumMb, MaximumMb);
	}

	public static string Format(int mb)
	{
		double gb = mb / 1024.0;
		return Math.Abs(gb - Math.Round(gb)) < 0.001 ? $"{gb:0} GB" : $"{gb:0.##} GB";
	}

	public static bool Apply(Process process, int? limitMb)
	{
		if (!Platform.IsWindows)
			return true;
		try
		{
			IntPtr handle = process.Handle;
			if (!GetProcessWorkingSetSizeEx(handle, out nuint minimum, out nuint maximum, out uint flags))
				return false;
			if (limitMb is not int mb)
			{
				if ((flags & QuotaLimitsHardwsMaxEnable) == 0)
					return true;
				bool removed = SetProcessWorkingSetSizeEx(handle, minimum, maximum, QuotaLimitsHardwsMinDisable | QuotaLimitsHardwsMaxDisable);
				if (removed)
					App.Logger.WriteLine("RobloxMemoryLimit", "Removed the Roblox memory limit");
				else
					App.Logger.WriteLine("RobloxMemoryLimit", "The Roblox memory limit could not be removed, error " + Marshal.GetLastPInvokeError());
				return removed;
			}
			ulong limitBytes = (ulong)Clamp(mb) * 1048576UL;
			if ((flags & QuotaLimitsHardwsMaxEnable) != 0 && maximum == (nuint)limitBytes)
				return true;
			ulong floor = Math.Min(Math.Max((ulong)minimum, MinimumWorkingSetBytes), limitBytes / 4);
			if (!SetProcessWorkingSetSizeEx(handle, (nuint)floor, (nuint)limitBytes, QuotaLimitsHardwsMinDisable | QuotaLimitsHardwsMaxEnable))
			{
				App.Logger.WriteLine("RobloxMemoryLimit", "The Roblox memory limit could not be applied, error " + Marshal.GetLastPInvokeError());
				return false;
			}
			App.Logger.WriteLine("RobloxMemoryLimit", "Roblox can now use at most " + Format(Clamp(mb)) + " of RAM, anything above that is paged out instead of crashing the game");
			return true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxMemoryLimit", "The Roblox memory limit could not be changed: " + ex.Message);
			return false;
		}
	}
}
