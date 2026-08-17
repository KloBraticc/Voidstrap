using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Voidstrap.Integrations.Overlays
{
    public static class RobloxPresentTracer
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
        private static TraceEventSession? _session;
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
                TraceEventSession? session;
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
            TraceEventSession? session = null;
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
                using var session = new TraceEventSession(sessionName);
                session.StopOnDispose = true;
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
                session.Source.Dynamic.All += OnTraceEvent;
                try
                {
                    session.Source.Process();
                }
                finally
                {
                    session.Source.Dynamic.All -= OnTraceEvent;
                }
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

        private static void OnTraceEvent(TraceEvent data)
        {
            int pid = Volatile.Read(ref _targetPid);
            bool dxgiPresent = data.ProviderGuid == DxgiProvider
                && ((int)data.ID == PresentStartEventId || (int)data.ID == PresentMultiplaneOverlayStartEventId);
            bool d3d9Present = data.ProviderGuid == D3d9Provider && (int)data.ID == D3d9PresentStartEventId;
            if (pid == 0 || data.ProcessID != pid || (!dxgiPresent && !d3d9Present) || Volatile.Read(ref _accepting) == 0)
                return;

            double timestamp = data.TimeStampRelativeMSec;
            lock (SampleLock)
            {
                ulong streamKey = GetStreamKey(data);
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

        private static ulong GetStreamKey(TraceEvent data)
        {
            try
            {
                object? value = data.PayloadByName("pIDXGISwapChain");
                if (value != null)
                    return ConvertPayloadKey(value);
            }
            catch
            {
            }
            foreach (string name in data.PayloadNames)
            {
                if (!name.Contains("SwapChain", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    object? value = data.PayloadByName(name);
                    if (value != null)
                        return ConvertPayloadKey(value);
                }
                catch
                {
                }
            }
            return 0;
        }

        private static ulong ConvertPayloadKey(object value)
        {
            return value is IntPtr pointer ? unchecked((ulong)pointer.ToInt64()) : Convert.ToUInt64(value);
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
			TraceEventSession? session;
			lock (LifetimeLock)
				session = _session;
			if (session != null)
				EnableProviderForTarget(session, currentPid);
        }

		private static void EnableProviderForTarget(TraceEventSession session, int processId)
		{
			if (processId <= 0)
				return;
			if (Interlocked.Exchange(ref _enabledPid, processId) == processId)
				return;
			TraceEventProviderOptions options = new()
			{
				ProcessIDFilter = [processId]
			};
			try
			{
				session.EnableProvider(DxgiProvider, TraceEventLevel.Verbose, DxgiEventsKeyword, options);
				session.EnableProvider(D3d9Provider, TraceEventLevel.Verbose, DxgiEventsKeyword, options);
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

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
