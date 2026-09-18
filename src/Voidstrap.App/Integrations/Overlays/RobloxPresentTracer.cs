using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Overlays
{
    public static partial class RobloxPresentTracer
    {
        private static readonly Guid DxgiProvider = new Guid("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
        private static readonly Guid D3d9Provider = new Guid("783aca0a-790e-4d7f-8451-aa850511c6b9");
        private static readonly object LifetimeLock = new object();
        private static readonly object SampleLock = new object();
        private sealed class PresentStream
        {
            public readonly double[] Times = new double[128];
            public int Head;
            public int Count;
            public double Last;
            public double LastSeen;
        }

        private static readonly Dictionary<ulong, PresentStream> PresentStreams = new Dictionary<ulong, PresentStream>();
        private const int PresentStartEventId = 42;
        private const int PresentMultiplaneOverlayStartEventId = 55;
        private const int D3d9PresentStartEventId = 1;
        private const ulong DxgiEventsKeyword = 0x2;
        private const double MeasurementWindowMs = 500.0;
        private const long StaleAfterMs = 1500;

        private static Thread? _thread;
        private static EtwSession? _session;
        private static int _references;
		private static IDisposable? _trackerLease;
		private static int _retryPending;
        private static int _stopGeneration;
        private static int _targetPid;
        private static int _accepting;
        private static double _intervalMs;
        private static double _lastPublishMs;
        private static long _lastEventTick;
        private static readonly PresentStream CaptureStream = new PresentStream();
        private static double _captureIntervalMs;
        private static long _lastCaptureEventTick;
        private static double _frameGenerationIntervalMs;
        private static long _lastFrameGenerationTick;
        private static int _needsElevation;

        private static int _enabledPid;

        private const int HealIntervalMs = 2000;

        private static Timer? _healTimer;

        private static void OnHealTick(object? state)
        {
            try
            {
                if (Volatile.Read(ref _enabledPid) != 0)
                    return;
                EtwSession? session;
                lock (LifetimeLock)
                {
                    if (_references == 0)
                        return;
                    session = _session;
                }
                if (session == null)
                    return;
                UpdateTarget(RobloxWindowTracker.Current);
                int pid = Volatile.Read(ref _targetPid);
                if (pid > 0)
                    EnableProviderForTarget(session, pid);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RobloxPresentTracer", "Present trace recovery failed: " + ex.Message);
            }
        }

        public static bool NeedsElevation => Volatile.Read(ref _needsElevation) != 0;

        public static bool Active
        {
            get
            {
                return Volatile.Read(ref _accepting) != 0
                    && ((Volatile.Read(ref _intervalMs) > 0.01
                    && Environment.TickCount64 - Volatile.Read(ref _lastEventTick) <= StaleAfterMs)
                    || (Volatile.Read(ref _captureIntervalMs) > 0.01
                    && Environment.TickCount64 - Volatile.Read(ref _lastCaptureEventTick) <= StaleAfterMs)
                    || (Volatile.Read(ref _frameGenerationIntervalMs) > 0.01
                    && Environment.TickCount64 - Volatile.Read(ref _lastFrameGenerationTick) <= StaleAfterMs));
            }
        }

        public static double IntervalMs
        {
            get
            {
                if (!Active)
                    return 0;
                double traced = Volatile.Read(ref _intervalMs);
                if (traced > 0.01 && Environment.TickCount64 - Volatile.Read(ref _lastEventTick) <= StaleAfterMs)
                    return traced;
                double frameGeneration = Volatile.Read(ref _frameGenerationIntervalMs);
                if (frameGeneration > 0.01 && Environment.TickCount64 - Volatile.Read(ref _lastFrameGenerationTick) <= StaleAfterMs)
                    return frameGeneration;
                return Volatile.Read(ref _captureIntervalMs);
            }
        }

        public static double FramesPerSecond
        {
            get
            {
                double interval = IntervalMs;
                return interval > 0.01 ? 1000.0 / interval : 0;
            }
        }

        public static void ReportCapturedFrame(double timestampMs)
        {
            if (timestampMs <= 0 || Volatile.Read(ref _accepting) == 0)
                return;
            lock (SampleLock)
            {
                AddSampleLocked(CaptureStream, timestampMs);
                Volatile.Write(ref _lastCaptureEventTick, Environment.TickCount64);
                double fps = CalculateStreamFps(CaptureStream, timestampMs);
                Volatile.Write(ref _captureIntervalMs, fps > 0 ? 1000.0 / fps : 0);
            }
        }

        public static void ReportFrameGenerationCadence(double fps)
        {
            if (fps < 5.0 || fps > 500.0 || Volatile.Read(ref _accepting) == 0)
            {
                Volatile.Write(ref _frameGenerationIntervalMs, 0);
                Volatile.Write(ref _lastFrameGenerationTick, 0);
                return;
            }
            Volatile.Write(ref _frameGenerationIntervalMs, 1000.0 / fps);
            Volatile.Write(ref _lastFrameGenerationTick, Environment.TickCount64);
        }

        public static void Start()
        {
            lock (LifetimeLock)
            {
                bool firstReference = _references++ == 0;
                if (firstReference)
                {
                    ResetSamples();
                    RobloxWindowTracker.Changed += OnTrackerChanged;
                    _trackerLease = RobloxWindowTracker.Acquire();
                    UpdateTarget(RobloxWindowTracker.Current);
                    _healTimer = new Timer(OnHealTick, null, HealIntervalMs, HealIntervalMs);
                }
                if (_thread != null)
                    return;
				StartThreadLocked();
            }
        }

		private static void StartThreadLocked()
		{
			_thread = new Thread(TraceLoop)
			{
				IsBackground = true,
				Name = "RobloxPresentTrace",
				Priority = ThreadPriority.BelowNormal,
			};
			_thread.Start();
		}

        public static void Stop()
        {
            EtwSession? session = null;
            IDisposable? trackerLease = null;
            lock (LifetimeLock)
            {
                if (_references == 0)
                    return;
                _references--;
                if (_references != 0)
                    return;
                RobloxWindowTracker.Changed -= OnTrackerChanged;
                trackerLease = _trackerLease;
                _trackerLease = null;
                _healTimer?.Dispose();
                _healTimer = null;
                Interlocked.Exchange(ref _enabledPid, 0);
                Volatile.Write(ref _accepting, 0);
                Interlocked.Increment(ref _stopGeneration);
                session = _session;
                ResetSamples();
            }
            trackerLease?.Dispose();
            try
            {
                session?.Stop();
            }
            catch
            {
            }
        }

        private static void TraceLoop()
        {
            string sessionName = "VoidstrapRobloxPresent" + Environment.ProcessId;
            int stopGeneration = Volatile.Read(ref _stopGeneration);
            try
            {
                using var session = EtwSession.Create(sessionName);
                lock (LifetimeLock)
                {
                    if (_references == 0)
                        return;
                    _session = session;
                }
				Interlocked.Exchange(ref _enabledPid, 0);
				UpdateTarget(RobloxWindowTracker.Current);
				int startupPid = Volatile.Read(ref _targetPid);
				App.Logger.WriteLine("RobloxPresentTracer", startupPid > 0
					? "Present trace session started, target pid " + startupPid
					: "Present trace session started with no Roblox window yet, waiting for the tracker");
				EnableProviderForTarget(session, startupPid);
                session.Process();
            }
            catch (UnauthorizedAccessException ex)
            {
                Volatile.Write(ref _needsElevation, 1);
                App.Logger.WriteLine("RobloxPresentTracer", "Present tracing needs administrator: " + ex.Message);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RobloxPresentTracer", "Present tracing unavailable: " + ex.Message);
            }
            finally
            {
				bool retry;
                bool restart;
                lock (LifetimeLock)
                {
                    _session = null;
                    _thread = null;
                    Interlocked.Exchange(ref _enabledPid, 0);
                    if (_references > 0)
                    {
                        ResetTracedSamples();
                        UpdateTarget(RobloxWindowTracker.Current);
                    }
                    else
                    {
                        Volatile.Write(ref _accepting, 0);
                        ResetSamples();
                    }
					restart = _references > 0 && Volatile.Read(ref _stopGeneration) != stopGeneration;
                    retry = _references > 0 && !restart;
                    if (restart)
                        StartThreadLocked();
                }
				if (retry)
					ScheduleRetry();
            }
        }

		private static void ScheduleRetry()
		{
			if (Interlocked.Exchange(ref _retryPending, 1) != 0)
				return;
			_ = RetryAsync();
		}

		private static async Task RetryAsync()
		{
			await Task.Delay(10000).ConfigureAwait(false);
			lock (LifetimeLock)
			{
				Interlocked.Exchange(ref _retryPending, 0);
				if (_references > 0 && _thread == null)
					StartThreadLocked();
			}
		}

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static unsafe void OnEventRecord(byte* record)
        {
            try
            {
                Guid provider = *(Guid*)(record + 24);
                int id = *(ushort*)(record + 40);
                int processId = *(int*)(record + 12);
                long timeStamp = *(long*)(record + 16);
                ushort userDataLength = *(ushort*)(record + 86);
                byte* userData = *(byte**)(record + 96);
                ulong streamKey = userData != null && userDataLength >= 8 ? *(ulong*)userData : 0;
                OnPresent(provider, id, processId, timeStamp * 1000.0 / Stopwatch.Frequency, streamKey);
            }
            catch
            {
            }
        }

        private static void OnPresent(Guid provider, int id, int processId, double timestamp, ulong streamKey)
        {
            int pid = Volatile.Read(ref _targetPid);
            bool dxgiPresent = provider == DxgiProvider
                && (id == PresentStartEventId || id == PresentMultiplaneOverlayStartEventId);
            bool d3d9Present = provider == D3d9Provider && id == D3d9PresentStartEventId;
            if (pid == 0 || processId != pid || (!dxgiPresent && !d3d9Present) || Volatile.Read(ref _accepting) == 0)
                return;

            lock (SampleLock)
            {
                if (!PresentStreams.TryGetValue(streamKey, out PresentStream? stream))
                {
                    if (PresentStreams.Count >= 16)
                        RemoveOldestStreamLocked();
                    stream = new PresentStream();
                    PresentStreams[streamKey] = stream;
                }
                if (!AddSampleLocked(stream, timestamp))
                    return;
                Volatile.Write(ref _lastEventTick, Environment.TickCount64);
                if (timestamp - _lastPublishMs >= 100.0)
                {
                    PublishIntervalLocked(timestamp);
                    _lastPublishMs = timestamp;
                }
            }
        }

        private static bool AddSampleLocked(PresentStream stream, double timestamp)
        {
            double delta = timestamp - stream.Last;
            if (stream.Last > 0 && delta < 0.5)
                return false;
            if (stream.Last > 0 && delta > StaleAfterMs)
            {
                stream.Head = 0;
                stream.Count = 0;
            }
            stream.Last = timestamp;
            stream.LastSeen = timestamp;
            stream.Times[stream.Head] = timestamp;
            stream.Head = (stream.Head + 1) % stream.Times.Length;
            if (stream.Count < stream.Times.Length)
                stream.Count++;
            return true;
        }

        private static void PublishIntervalLocked(double newest)
        {
            double bestFps = 0;
            foreach (PresentStream stream in PresentStreams.Values)
            {
                if (newest - stream.LastSeen > MeasurementWindowMs)
                    continue;
                double fps = CalculateStreamFps(stream, newest);
                if (fps > bestFps)
                    bestFps = fps;
            }
            Volatile.Write(ref _intervalMs, bestFps > 0 ? 1000.0 / bestFps : 0);
        }

        private static double CalculateStreamFps(PresentStream stream, double newest)
        {
            if (stream.Count == 0 || newest - stream.LastSeen > MeasurementWindowMs)
                return 0;
            double oldest = stream.LastSeen;
            int samples = 1;
            for (int offset = 1; offset < stream.Count; offset++)
            {
                int index = (stream.Head - 1 - offset + stream.Times.Length) % stream.Times.Length;
                double timestamp = stream.Times[index];
                if (stream.LastSeen - timestamp > MeasurementWindowMs)
                    break;
                oldest = timestamp;
                samples++;
            }
            double span = stream.LastSeen - oldest;
            if (samples < 4 || span <= 1.0)
                return 0;
            double fps = (samples - 1) * 1000.0 / span;
            return fps >= 5.0 && fps <= 500.0 ? fps : 0;
        }

        private static void RemoveOldestStreamLocked()
        {
            ulong oldestKey = 0;
            double oldestTime = double.MaxValue;
            foreach (KeyValuePair<ulong, PresentStream> pair in PresentStreams)
            {
                if (pair.Value.LastSeen < oldestTime)
                {
                    oldestTime = pair.Value.LastSeen;
                    oldestKey = pair.Key;
                }
            }
            PresentStreams.Remove(oldestKey);
        }

        private static void OnTrackerChanged(object? sender, RobloxWindowRect rect)
        {
            UpdateTarget(rect);
			int currentPid = Volatile.Read(ref _targetPid);
			if (currentPid == 0)
				return;
			EtwSession? session;
			lock (LifetimeLock)
				session = _session;
			if (session != null)
				EnableProviderForTarget(session, currentPid);
        }

		private static void EnableProviderForTarget(EtwSession session, int processId)
		{
			if (processId <= 0)
				return;
			if (Interlocked.Exchange(ref _enabledPid, processId) == processId)
				return;
			try
			{
				session.EnableProvider(DxgiProvider, DxgiEventsKeyword, processId);
				session.EnableProvider(D3d9Provider, DxgiEventsKeyword, processId);
				App.Logger.WriteLine("RobloxPresentTracer", "Tracing Present events for Roblox pid " + processId);
			}
			catch (Exception ex)
			{
				Interlocked.Exchange(ref _enabledPid, 0);
				App.Logger.WriteLine("RobloxPresentTracer", "Could not enable Present tracing for pid " + processId + ": " + ex.Message);
			}
		}

        private static void UpdateTarget(RobloxWindowRect rect)
        {
            int pid = 0;
            if (rect.Hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(rect.Hwnd, out uint targetPid);
                if (targetPid <= int.MaxValue)
                    pid = (int)targetPid;
            }
            int accepting = rect.Valid && pid != 0 ? 1 : 0;
            int oldPid = Interlocked.Exchange(ref _targetPid, pid);
            int oldAccepting = Interlocked.Exchange(ref _accepting, accepting);
            if (pid != oldPid || accepting != oldAccepting)
                ResetSamples();
        }

        private static void ResetSamples()
        {
            lock (SampleLock)
                ResetSamplesLocked();
        }

        private static void ResetTracedSamples()
        {
            lock (SampleLock)
            {
                PresentStreams.Clear();
                _lastPublishMs = 0;
                Volatile.Write(ref _intervalMs, 0);
                Volatile.Write(ref _lastEventTick, 0);
            }
        }

        private static void ResetSamplesLocked()
        {
            PresentStreams.Clear();
            CaptureStream.Head = 0;
            CaptureStream.Count = 0;
            CaptureStream.Last = 0;
            CaptureStream.LastSeen = 0;
            _lastPublishMs = 0;
            Volatile.Write(ref _intervalMs, 0);
            Volatile.Write(ref _lastEventTick, 0);
            Volatile.Write(ref _captureIntervalMs, 0);
            Volatile.Write(ref _lastCaptureEventTick, 0);
            Volatile.Write(ref _frameGenerationIntervalMs, 0);
            Volatile.Write(ref _lastFrameGenerationTick, 0);
        }

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private sealed unsafe partial class EtwSession : IDisposable
        {
            private const int PropertiesSize = 120;
            private const int NameBytes = 1024;
            private const int LogFileSize = 448;
            private const uint ErrorAlreadyExists = 183;
            private const uint ErrorAccessDenied = 5;
            private const uint ErrorCancelled = 1223;
            private const ulong InvalidTraceHandle = ulong.MaxValue;

            private readonly string _name;
            private readonly ulong _handle;
            private readonly object _stateLock = new object();
            private ulong _consumer = InvalidTraceHandle;
            private bool _stopped;

            private EtwSession(string name, ulong handle)
            {
                _name = name;
                _handle = handle;
            }

            public static EtwSession Create(string name)
            {
                byte* properties = NewProperties();
                try
                {
                    uint status = StartTrace(out ulong handle, name, properties);
                    if (status == ErrorAlreadyExists)
                    {
                        ResetProperties(properties);
                        ControlTrace(0, name, properties, 1);
                        ResetProperties(properties);
                        status = StartTrace(out handle, name, properties);
                    }
                    if (status == ErrorAccessDenied)
                        throw new UnauthorizedAccessException("Starting an ETW session requires administrator rights");
                    if (status != 0)
                        throw new InvalidOperationException("StartTrace failed with error " + status);
                    return new EtwSession(name, handle);
                }
                finally
                {
                    NativeMemory.Free(properties);
                }
            }

            public void EnableProvider(Guid provider, ulong keyword, int processId)
            {
                uint pid = (uint)processId;
                byte* filter = stackalloc byte[16];
                *(ulong*)filter = (ulong)&pid;
                *(uint*)(filter + 8) = sizeof(uint);
                *(uint*)(filter + 12) = 0x80000004;
                byte* parameters = stackalloc byte[48];
                new Span<byte>(parameters, 48).Clear();
                *(uint*)parameters = 2;
                *(ulong*)(parameters + 32) = (ulong)filter;
                *(uint*)(parameters + 40) = 1;
                uint status = EnableTraceEx2(_handle, &provider, 1, 5, keyword, 0, 0, parameters);
                if (status != 0)
                    throw new InvalidOperationException("EnableTraceEx2 failed with error " + status);
            }

            public void Process()
            {
                IntPtr namePointer = Marshal.StringToHGlobalUni(_name);
                byte* logFile = (byte*)NativeMemory.AllocZeroed(LogFileSize);
                try
                {
                    *(IntPtr*)(logFile + 8) = namePointer;
                    *(uint*)(logFile + 28) = 0x100 | 0x10000000;
                    *(IntPtr*)(logFile + 424) = (IntPtr)(delegate* unmanaged[Stdcall]<byte*, void>)&OnEventRecord;
                    ulong consumer = OpenTrace(logFile);
                    if (consumer == InvalidTraceHandle)
                        throw new InvalidOperationException("OpenTrace failed with error " + Marshal.GetLastPInvokeError());
                    lock (_stateLock)
                    {
                        if (_stopped)
                        {
                            CloseTrace(consumer);
                            return;
                        }
                        _consumer = consumer;
                    }
                    uint status = ProcessTrace(&consumer, 1, IntPtr.Zero, IntPtr.Zero);
                    if (status != 0 && status != ErrorCancelled)
                        App.Logger.WriteLine("RobloxPresentTracer", "ProcessTrace ended with error " + status);
                }
                finally
                {
                    NativeMemory.Free(logFile);
                    Marshal.FreeHGlobal(namePointer);
                }
            }

            public void Stop()
            {
                ulong consumer;
                lock (_stateLock)
                {
                    if (_stopped)
                        return;
                    _stopped = true;
                    consumer = _consumer;
                    _consumer = InvalidTraceHandle;
                }
                byte* properties = NewProperties();
                try
                {
                    ControlTrace(_handle, null, properties, 1);
                }
                finally
                {
                    NativeMemory.Free(properties);
                }
                if (consumer != InvalidTraceHandle)
                    CloseTrace(consumer);
            }

            public void Dispose() => Stop();

            private static byte* NewProperties()
            {
                byte* properties = (byte*)NativeMemory.Alloc(PropertiesSize + NameBytes);
                ResetProperties(properties);
                return properties;
            }

            private static void ResetProperties(byte* properties)
            {
                new Span<byte>(properties, PropertiesSize + NameBytes).Clear();
                *(uint*)properties = PropertiesSize + NameBytes;
                *(uint*)(properties + 40) = 1;
                *(uint*)(properties + 44) = 0x20000;
                *(uint*)(properties + 64) = 0x100;
                *(uint*)(properties + 116) = PropertiesSize;
            }

            [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
            private static partial uint StartTrace(out ulong handle, string name, byte* properties);

            [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
            private static partial uint ControlTrace(ulong handle, string? name, byte* properties, uint controlCode);

            [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
            private static partial uint EnableTraceEx2(ulong handle, Guid* provider, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, byte* parameters);

            [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
            private static partial ulong OpenTrace(byte* logFile);

            [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace")]
            private static partial uint ProcessTrace(ulong* handles, uint count, IntPtr startTime, IntPtr endTime);

            [LibraryImport("advapi32.dll", EntryPoint = "CloseTrace")]
            private static partial uint CloseTrace(ulong handle);
        }
    }
}

