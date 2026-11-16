using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

internal static unsafe partial class WebViewLockRelease
{
    private const int MaximumLockFiles = 64;

    private const string WebViewProcessName = "msedgewebview2";

    internal static void CloseWebViewLocking(string directory)
    {
        const string LOG_IDENT = "WebViewLockRelease::CloseWebViewLocking";
        if (!Platform.IsWindows || !Directory.Exists(directory))
            return;
        string[] lockFiles;
        try
        {
            lockFiles = Directory.EnumerateFiles(directory, "lockfile", SearchOption.AllDirectories).Take(MaximumLockFiles).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (lockFiles.Length == 0)
            return;
        foreach (int processId in FindLockingProcesses(lockFiles))
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (!string.Equals(process.ProcessName, WebViewProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;
                App.Logger?.WriteLine(LOG_IDENT, "Closing the leftover web view process " + processId + " that still holds Voidstrap data");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not close the web view process " + processId + ": " + ex.Message);
            }
        }
    }

    private static List<int> FindLockingProcesses(string[] files)
    {
        List<int> result = new();
        char* key = stackalloc char[RmSessionKeyLength + 1];
        if (RmStartSession(out uint session, 0, key) != 0)
            return result;
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                return result;
            uint count = 0;
            int status = RmGetList(session, out uint needed, ref count, null, out _);
            if (status != ErrorMoreData || needed == 0)
                return result;
            RmProcessInfo[] processes = new RmProcessInfo[needed];
            count = needed;
            fixed (RmProcessInfo* buffer = processes)
            {
                if (RmGetList(session, out _, ref count, buffer, out _) != 0)
                    return result;
            }
            for (int i = 0; i < count; i++)
                result.Add(processes[i].ProcessId);
        }
        finally
        {
            RmEndSession(session);
        }
        return result;
    }

    private const int RmSessionKeyLength = 32;

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmProcessInfo
    {
        public int ProcessId;
        public uint StartTimeLow;
        public uint StartTimeHigh;
        public fixed char AppName[256];
        public fixed char ServiceShortName[64];
        public int ApplicationType;
        public int AppStatus;
        public int SessionId;
        public int Restartable;
    }

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmStartSession")]
    private static partial int RmStartSession(out uint session, uint flags, char* sessionKey);

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmEndSession")]
    private static partial int RmEndSession(uint session);

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmRegisterResources", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);

    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmGetList")]
    private static partial int RmGetList(uint session, out uint needed, ref uint count, RmProcessInfo* processes, out uint rebootReasons);
}
