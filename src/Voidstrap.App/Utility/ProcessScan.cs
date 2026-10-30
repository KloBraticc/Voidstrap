using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

public static partial class ProcessScan
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const uint StillActive = 259;

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static int Count(string name)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch
        {
            return 0;
        }
        foreach (Process process in processes)
            process.Dispose();
        return processes.Length;
    }

    public static bool Any(string name) => Count(name) > 0;

    public static bool IsAlive(int processId)
    {
        if (processId <= 0)
            return false;
        if (Platform.IsWindows)
        {
            nint handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (handle == 0)
                return Marshal.GetLastPInvokeError() == 5;
            try
            {
                return GetExitCodeProcess(handle, out uint exitCode) && exitCode == StillActive;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        if (Platform.IsLinux)
            return System.IO.Directory.Exists("/proc/" + processId);
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
