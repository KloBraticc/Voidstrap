using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility
{
    public static partial class MemoryManager
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public nuint PeakWorkingSetSize;
            public nuint WorkingSetSize;
            public nuint QuotaPeakPagedPoolUsage;
            public nuint QuotaPagedPoolUsage;
            public nuint QuotaPeakNonPagedPoolUsage;
            public nuint QuotaNonPagedPoolUsage;
            public nuint PagefileUsage;
            public nuint PeakPagefileUsage;
            public nuint PrivateUsage;
            public nuint PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [LibraryImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

        [LibraryImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSizeEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessWorkingSetSizeEx(IntPtr process, nint minimum, nint maximum, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetLastInputInfo(ref LastInputInfo info);

        private static readonly IntPtr CurrentProcess = new IntPtr(-1);

        private const int TrimLoopMs = 1000;
        private const int TrimIdleMs = 800;
        private const int TrimMinIntervalMs = 2000;
        private const long TrimThresholdBytes = 16L * 1024 * 1024;

        private static CancellationTokenSource? _trimCts;
        private static Task? _trimTask;
        private static long _lastTrimTicks;

        private static void StartTrimLoop()
        {
            if (!Platform.IsWindows || _trimCts != null)
                return;
            CancellationTokenSource cts = new CancellationTokenSource();
            _trimCts = cts;
            _trimTask = Task.Run(() => TrimLoopAsync(cts.Token));
        }

        private static void StopTrimLoop()
        {
            CancellationTokenSource? cts = _trimCts;
            Task? task = _trimTask;
            _trimCts = null;
            _trimTask = null;
            Cancel(cts);
            Wait(task);
            cts?.Dispose();
        }

        private static async Task TrimLoopAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(StartupDelayMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            while (!token.IsCancellationRequested)
            {
                try
                {
                    TrimIfIdle();
                }
                catch
                {
                }
                try
                {
                    await Task.Delay(TrimLoopMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static void TrimIfIdle()
        {
            long now = Environment.TickCount64;
            if (now - Volatile.Read(ref _lastTrimTicks) < TrimMinIntervalMs)
                return;
            LastInputInfo input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (IsAppForeground() && GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Time) < TrimIdleMs)
                return;
            ProcessMemoryCounters counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (!GetProcessMemoryInfo(CurrentProcess, ref counters, counters.Size))
                return;
            if ((long)counters.PrivateWorkingSetSize < TrimThresholdBytes)
                return;
            SetProcessWorkingSetSizeEx(CurrentProcess, -1, -1, 0);
            Volatile.Write(ref _lastTrimTicks, now);
        }
    }
}
