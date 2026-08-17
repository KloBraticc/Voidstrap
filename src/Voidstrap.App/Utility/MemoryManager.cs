using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility
{
    public static class MemoryManager
    {
        public enum MemoryTier
        {
            Active,
            Light,
            Medium,
            Deep
        }

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        private const uint PROCESS_MODE_BACKGROUND_BEGIN = 1048576u;
        private const uint PROCESS_MODE_BACKGROUND_END = 2097152u;

        private const int LightMs = 10000;
        private const int DeepMs = 60000;
        private const int MinTrimIntervalMs = 8000;
        private const int BackgroundLoopMs = 60000;
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
            EndBackgroundMode();
            _currentTier = MemoryTier.Active;
        }

        public static void SetActive()
        {
            CancelEscalation();

            if (_bgModeSet)
            {
                EndBackgroundMode();
            }

            if (_currentTier == MemoryTier.Active)
                return;

            _currentTier = MemoryTier.Active;
        }

        public static void SetGameplayActive(bool active)
        {
            Volatile.Write(ref _gameplayActive, active ? 1 : 0);
            if (active)
            {
                SetActive();
            }
        }

        public static void SetBackground()
        {
            if (Volatile.Read(ref _gameplayActive) != 0)
            {
                SetActive();
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
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: true);
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
            if (_bgModeSet)
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

        private static void EndBackgroundMode()
        {
            if (!_bgModeSet)
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
                try
                {
                    if (Volatile.Read(ref _gameplayActive) != 0 || IsAppForeground())
                    {
                        if (_currentTier != MemoryTier.Active || _bgModeSet)
                            SetActive();
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
                    await Task.Delay(BackgroundLoopMs, token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
            }
        }

        private static void Trim()
        {
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
                    using Process process = Process.GetCurrentProcess();
                    EmptyWorkingSet(process.Handle);
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
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                    return false;
                GetWindowThreadProcessId(foreground, out uint pid);
                return pid == (uint)Environment.ProcessId;
            }
            catch
            {
                return false;
            }
        }

    }
}
