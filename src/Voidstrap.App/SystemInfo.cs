using System;
using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

public static partial class SystemInfo
{
	public struct SYSTEM_INFO
	{
		public ushort wProcessorArchitecture;

		public ushort wReserved;

		public uint dwPageSize;

		public nint lpMinimumApplicationAddress;

		public nint lpMaximumApplicationAddress;

		public nint dwActiveProcessorMask;

		public uint dwNumberOfProcessors;

		public uint dwProcessorType;

		public uint dwAllocationGranularity;

		public ushort wProcessorLevel;

		public ushort wProcessorRevision;
	}

	[LibraryImport("kernel32.dll")]
	private static partial void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

	public static int GetLogicalProcessorCount()
	{
		if (OperatingSystem.IsWindows())
		{
			try
			{
				GetSystemInfo(out var lpSystemInfo);
				if (lpSystemInfo.dwNumberOfProcessors > 0)
				{
					return (int)lpSystemInfo.dwNumberOfProcessors;
				}
			}
			catch (EntryPointNotFoundException)
			{
			}
			catch (DllNotFoundException)
			{
			}
		}

		return Environment.ProcessorCount;
	}
}
