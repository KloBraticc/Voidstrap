using System.Runtime.InteropServices;

public static class SystemInfo
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

	[DllImport("kernel32.dll")]
	private static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

	public static int GetLogicalProcessorCount()
	{
		GetSystemInfo(out var lpSystemInfo);
		return (int)lpSystemInfo.dwNumberOfProcessors;
	}
}
