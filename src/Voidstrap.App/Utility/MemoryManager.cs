using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility
{
    public static partial class MemoryManager
    {
        public enum MemoryTier
        {
            Active,
            Light,
            Medium,
            Deep
        }

        [LibraryImport("psapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EmptyWorkingSet(IntPtr hProcess);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessInformation(IntPtr hProcess, int informationClass, ref PowerThrottlingState information, uint size);

        [LibraryImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessMemoryPriority(IntPtr hProcess, int informationClass, ref MemoryPriorityInformation information, uint size);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            public uint Version;

            public uint ControlMask;

            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryPriorityInformation
        {
            public uint MemoryPriority;
        }

        private const uint PROCESS_MODE_BACKGROUND_BEGIN = 1048576u;
        private const uint PROCESS_MODE_BACKGROUND_END = 2097152u;

        private const uint IDLE_PRIORITY_CLASS = 0x40u;
        private const uint NORMAL_PRIORITY_CLASS = 0x20u;

        private const int ProcessPowerThrottling = 4;
        private const int ProcessMemoryPriority = 0;
        private const uint PowerThrottlingCurrentVersion = 1;
        private const uint ExecutionSpeed = 0x1;
        private const uint MemoryPriorityVeryLow = 1;
        private const uint MemoryPriorityNormal = 5;

        private const int LightMs = 10000;
        private const int DeepMs = 60000;
        private const int MinTrimIntervalMs = 8000;
        private const int BackgroundLoopMs = 60000;
        private const int GameplayLoopMs = 30000;
        private const int StartupDelayMs = 20000;

        private static readonly object _sync = new();
        private static long _lastTrimTicks;
        private static volatile MemoryTier _currentTier = MemoryTier.Active;
        private static bool _bgModeSet;

        private static CancellationTokenSource? _escalationCts;
        private static CancellationTokenSource? _loopCts;
        private static Task? _escalationTask;
        private static Task? _loopTask;
        private static Task? _trimTask;
        private static int _trimRunning;
        private static int _gameplayActive;
        private static bool _efficiencyModeSet;
        private static bool _idlePrioritySet;
        private static bool _lowMemoryPrioritySet;

        private static int _windowActive = 1;

        public static void Start()
        {
            lock (_sync)
            {
                if (_loopCts != null)
                    return;
                CancellationTokenSource cts = new CancellationTokenSource();
                _loopCts = cts;
                _loopTask = Task.Run(() => BackgroundLoopAsync(cts.Token));
            }
        }

        public static void Shutdown()
        {
            CancellationTokenSource? loopCts;
            CancellationTokenSource? escalationCts;
            Task? loopTask;
            Task? escalationTask;
            Task? trimTask;
            lock (_sync)
            {
                loopCts = _loopCts;
                _loopCts = null;
                loopTask = _loopTask;
                _loopTask = null;
                escalationCts = _escalationCts;
                _escalationCts = null;
                escalationTask = _escalationTask;
                _escalationTask = null;
                trimTask = _trimTask;
                _trimTask = null;
            }

            Cancel(loopCts);
            Cancel(escalationCts);
            Wait(loopTask);
            Wait(escalationTask);
            Wait(trimTask);
            loopCts?.Dispose();
            escalationCts?.Dispose();
            SetEfficiencyMode(false);
            SetLowMemoryPriority(false);
            EndBackgroundMode();
            SetIdlePriority(false);
            _currentTier = MemoryTier.Active;
        }

        public static void SetActive()
        {
            Volatile.Write(ref _windowActive, 1);
            CancelEscalation();

            SetEfficiencyMode(false);
            SetLowMemoryPriority(false);

            if (_bgModeSet)
            {
                EndBackgroundMode();
            }

            SetIdlePriority(false);

            if (_currentTier == MemoryTier.Active)
                return;

            _currentTier = MemoryTier.Active;
        }

        public static void SetGameplayActive(bool active)
        {
            Volatile.Write(ref _gameplayActive, active ? 1 : 0);
            if (!active)
            {
                SetActive();
                return;
            }
            if (!IsAppForeground())
            {
                EnterQuietMode();
            }
        }

        private static bool ServesLiveTraffic => Voidstrap.Integrations.AssetProxy.AssetProxyServer.IsRunning;

        public static void LeaveQuietModeForLiveTraffic()
        {
            CancelEscalation();
            SetEfficiencyMode(false);
            SetLowMemoryPriority(false);
            EndBackgroundMode();
            SetIdlePriority(false);
        }

        private static void EnterQuietMode()
        {
            if (ServesLiveTraffic)
            {
                LeaveQuietModeForLiveTraffic();
                return;
            }
            CancelEscalation();
            EndBackgroundMode();
            SetIdlePriority(true);
            SetLowMemoryPriority(true);
            SetEfficiencyMode(true);
            if (_currentTier != MemoryTier.Deep)
            {
                _currentTier = MemoryTier.Deep;
                ApplyTier(MemoryTier.Deep);
            }
        }

        public static void SetBackground()
        {
            Volatile.Write(ref _windowActive, 0);
            if (Volatile.Read(ref _gameplayActive) != 0)
            {
                EnterQuietMode();
                return;
            }
            CancelEscalation();

            _currentTier = MemoryTier.Light;
            ApplyTier(MemoryTier.Light);

            var cts = new CancellationTokenSource();
            CancellationToken token;
            lock (_sync)
            {
                _escalationCts = cts;
                token = cts.Token;
            }

            Task task = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(LightMs, token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (_currentTier >= MemoryTier.Medium)
                    return;

                _currentTier = MemoryTier.Medium;
                ApplyTier(MemoryTier.Medium);

                try
                {
                    await Task.Delay(DeepMs - LightMs, token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (_currentTier >= MemoryTier.Deep)
                    return;

                _currentTier = MemoryTier.Deep;
                ApplyTier(MemoryTier.Deep);
            });

            lock (_sync)
            {
                if (ReferenceEquals(_escalationCts, cts))
                    _escalationTask = task;
            }
        }

        private static void ApplyTier(MemoryTier tier)
        {
            switch (tier)
            {
                case MemoryTier.Light:
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    break;

                case MemoryTier.Medium:
                    TryTrimImageCache(2L * 1024 * 1024);
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    Trim();
                    BeginBackgroundMode();
                    break;

                case MemoryTier.Deep:
                    TryTrimImageCache(0L);
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    Trim();
                    BeginBackgroundMode();
                    break;
            }
        }

        private static void TryTrimImageCache(long targetBytes)
        {
            try
            {
                DynamicRenderSystem.TrimCache(targetBytes);
            }
            catch
            {
            }
        }

        private static void BeginBackgroundMode()
        {
            if (_bgModeSet || !Platform.IsWindows || Volatile.Read(ref _gameplayActive) != 0 || ServesLiveTraffic)
                return;
            try
            {
                using Process self = Process.GetCurrentProcess();
                _bgModeSet = SetPriorityClass(self.Handle, PROCESS_MODE_BACKGROUND_BEGIN);
            }
            catch
            {
            }
        }

        private static void SetIdlePriority(bool enabled)
        {
            if (_idlePrioritySet == enabled || !Platform.IsWindows || _bgModeSet || enabled && ServesLiveTraffic)
                return;
            try
            {
                using Process self = Process.GetCurrentProcess();
                if (SetPriorityClass(self.Handle, enabled ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS))
                {
                    _idlePrioritySet = enabled;
                }
            }
            catch
            {
            }
        }

        private static void SetLowMemoryPriority(bool enabled)
        {
            if (_lowMemoryPrioritySet == enabled || !Platform.IsWindows || enabled && ServesLiveTraffic)
                return;
            MemoryPriorityInformation information = new MemoryPriorityInformation
            {
                MemoryPriority = enabled ? MemoryPriorityVeryLow : MemoryPriorityNormal
            };
            try
            {
                using Process self = Process.GetCurrentProcess();
                if (SetProcessMemoryPriority(self.Handle, ProcessMemoryPriority, ref information, (uint)Marshal.SizeOf<MemoryPriorityInformation>()))
                {
                    _lowMemoryPrioritySet = enabled;
                }
            }
            catch
            {
            }
        }

        private static void SetEfficiencyMode(bool enabled)
        {
            if (_efficiencyModeSet == enabled || !Platform.IsWindows || enabled && ServesLiveTraffic)
                return;
            PowerThrottlingState state = new PowerThrottlingState
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = enabled ? ExecutionSpeed : 0u,
                StateMask = enabled ? ExecutionSpeed : 0u
            };
            try
            {
                using Process self = Process.GetCurrentProcess();
                if (SetProcessInformation(self.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
                {
                    _efficiencyModeSet = enabled;
                }
            }
            catch
            {
            }
        }

        private static void EndBackgroundMode()
        {
            if (!_bgModeSet || !Platform.IsWindows)
                return;
            try
            {
                using Process self = Process.GetCurrentProcess();
                if (SetPriorityClass(self.Handle, PROCESS_MODE_BACKGROUND_END))
                {
                    _bgModeSet = false;
                }
            }
            catch
            {
            }
        }

        private static void CancelEscalation()
        {
            CancellationTokenSource? cts;
            lock (_sync)
            {
                cts = _escalationCts;
                _escalationCts = null;
                _escalationTask = null;
            }
            Cancel(cts);
            cts?.Dispose();
        }

        private static async Task BackgroundLoopAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(StartupDelayMs, token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            while (!token.IsCancellationRequested)
            {
                bool gameplay = Volatile.Read(ref _gameplayActive) != 0;
                try
                {
                    if (IsAppForeground())
                    {
                        if (_currentTier != MemoryTier.Active || _bgModeSet || _efficiencyModeSet || _idlePrioritySet || _lowMemoryPrioritySet)
                            SetActive();
                    }
                    else if (gameplay)
                    {
                        EnterQuietMode();
                        Trim();
                    }
                    else if (_currentTier == MemoryTier.Active)
                    {
                        SetBackground();
                    }
                }
                catch
                {
                }
                try
                {
                    await Task.Delay(gameplay ? GameplayLoopMs : BackgroundLoopMs, token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
            }
        }

        private static void Trim()
        {
            if (ServesLiveTraffic)
                return;
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastTrimTicks) < MinTrimIntervalMs)
                return;
            if (Interlocked.CompareExchange(ref _trimRunning, 1, 0) != 0)
                return;
            Interlocked.Exchange(ref _lastTrimTicks, now);

            Task task = Task.Run(() =>
            {
                try
                {
                    if (Platform.IsWindows)
                    {
                        using Process process = Process.GetCurrentProcess();
                        EmptyWorkingSet(process.Handle);
                    }
                    else
                    {
                        ReleaseUnusedMemory();
                    }
                }
                catch
                {
                }
                finally
                {
                    Volatile.Write(ref _trimRunning, 0);
                }
            });
            lock (_sync)
                _trimTask = task;
        }

        private static void ReleaseUnusedMemory()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch
            {
            }
        }

        private static void Cancel(CancellationTokenSource? cts)
        {
            try
            {
                cts?.Cancel();
            }
            catch
            {
            }
        }

        private static void Wait(Task? task)
        {
            if (task == null)
                return;
            try
            {
                task.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        private static bool IsAppForeground()
        {
            if (!Platform.IsWindows)
                return IsManagedWindowActive();

            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                    return IsManagedWindowActive();
                GetWindowThreadProcessId(foreground, out uint pid);
                return pid == (uint)Environment.ProcessId;
            }
            catch
            {
                return IsManagedWindowActive();
            }
        }

        private static bool IsManagedWindowActive()
        {
            return Volatile.Read(ref _windowActive) != 0;
        }

    }
}
