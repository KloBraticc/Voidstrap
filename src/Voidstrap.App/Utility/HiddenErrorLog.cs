using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace Voidstrap.Utility;

internal static partial class HiddenErrorLog
{
    private const string LOG_IDENT = "HiddenErrors";

    private const int MaxDistinctSites = 300;

    private const int MaxMessageLength = 300;

    private const int MaxFreezeReports = 50;

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(500);

    private static readonly long FreezeThreshold = TimeSpan.FromSeconds(2).Ticks;

    private static readonly long StuckThreshold = TimeSpan.FromSeconds(5).Ticks;

    private static readonly DateTime SessionStarted = DateTime.UtcNow;

    private static readonly Regex HashCodePattern = new(@"\s*\(HashCode=\d+\)", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, int> Sites = new(StringComparer.Ordinal);

    private static int _installed;

    private static int _overflowReported;

    private static long _total;

    private static Dispatcher? _dispatcher;

    private static Timer? _watchdog;

    private static long _probeSentTicks;

    private static int _probePending;

    private static int _stuckReported;

    private static int _freezeReports;

    private static long _longestFreezeTicks;

    private static string _activeWindow = "";

    private static string _longestFreezeWindow = "";

    private static DispatcherOperation? _currentOperation;

    private static long _operationStartedTicks;

    private static string _lastSlowWork = "";

    private const int MaxStackCaptures = 8;

    private static int _stackCaptures;

    private static int _uiThreadId;

    private static int _stackCapturedForProbe;

    private static string _frozenStack = "";

    private const int ResourceCheckTicks = 60;

    private const int StarvationQueueLimit = 500;

    private const int StarvationTicksBeforeReport = 6;

    private const int MaxResourceReports = 20;

    private const int HandleWarnStep = 5000;

    private const int GdiWarnStep = 2500;

    private const long HeapWarnStepBytes = 256L * 1024 * 1024;

    private static int _watchdogTicks;

    private static int _starvedTicks;

    private static int _starvationReported;

    private static int _resourceReports;

    private static int _nextHandleWarn = HandleWarnStep;

    private static int _nextGdiWarn = GdiWarnStep;

    private static int _nextUserWarn = GdiWarnStep;

    private static long _nextHeapWarn;

    private static long _baselineHeap;

    private static int _peakHandles;

    private static int _peakGdi;

    private static int _peakUser;

    [ThreadStatic]
    private static bool _inside;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "GetGuiResources")]
    private static partial uint GetGuiResources(nint process, uint flags);

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        try
        {
            PresentationTraceSources.Refresh();
            Listen(PresentationTraceSources.DataBindingSource, "BindingErrors");
            Listen(PresentationTraceSources.ResourceDictionarySource, "ResourceErrors");
            Listen(PresentationTraceSources.AnimationSource, "AnimationErrors");
            Listen(PresentationTraceSources.DependencyPropertySource, "PropertyErrors");
            Listen(PresentationTraceSources.FreezableSource, "FreezableErrors");
            Listen(PresentationTraceSources.MarkupSource, "MarkupErrors");
            Listen(PresentationTraceSources.NameScopeSource, "NameScopeErrors");
            Listen(PresentationTraceSources.HwndHostSource, "HwndHostErrors");
            Trace.Listeners.Add(new DiagnosticListener("TraceErrors", true));
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "WPF error reporting could not start: " + ex.Message);
        }
        StartWatchdog();
    }

    public static void Uninstall()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0)
            return;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        Timer? watchdog = Interlocked.Exchange(ref _watchdog, null);
        watchdog?.Dispose();
        Dispatcher? dispatcher = Interlocked.Exchange(ref _dispatcher, null);
        if (dispatcher != null)
        {
            dispatcher.Hooks.OperationStarted -= OnOperationStarted;
            dispatcher.Hooks.OperationCompleted -= OnOperationFinished;
            dispatcher.Hooks.OperationAborted -= OnOperationFinished;
        }
        WriteSummary();
    }

    private static void Listen(TraceSource source, string category)
    {
        source.Switch.Level = SourceLevels.Error;
        source.Listeners.Add(new DiagnosticListener(category, false));
    }

    private static void StartWatchdog()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _uiThreadId = Environment.CurrentManagedThreadId;
#if !CROSSPLAT
            if (OperatingSystem.IsWindows())
                NativeStack.RememberCurrentThread();
#endif
            _dispatcher.Hooks.OperationStarted += OnOperationStarted;
            _dispatcher.Hooks.OperationCompleted += OnOperationFinished;
            _dispatcher.Hooks.OperationAborted += OnOperationFinished;
            _watchdog = new Timer(OnWatchdogTick, null, WatchdogInterval, WatchdogInterval);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The freeze watchdog could not start: " + ex.Message);
        }
    }

    private static void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        Interlocked.Exchange(ref _operationStartedTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _currentOperation, e.Operation);
    }

    private static void OnOperationFinished(object? sender, DispatcherHookEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _currentOperation, null, e.Operation) != e.Operation)
            return;
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _operationStartedTicks) >= FreezeThreshold)
            _lastSlowWork = Describe(e.Operation);
    }

    private static string DescribeCurrentWork()
    {
        return Describe(Volatile.Read(ref _currentOperation));
    }

    private static string Describe(DispatcherOperation? operation)
    {
        try
        {
            if (operation == null)
                return "";
            System.Reflection.MethodInfo? method = (
                operation
                    .GetType()
                    .GetField("_method", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(operation) as Delegate
            )?.Method;
            if (method == null)
                return " while running " + operation.Priority + " work";
            return " while running " + (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
        }
        catch
        {
            return "";
        }
    }

    private static void OnWatchdogTick(object? state)
    {
        try
        {
            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            CheckThreadPool();
            if (Interlocked.Increment(ref _watchdogTicks) % ResourceCheckTicks == 0)
                CheckResources();
            long now = DateTime.UtcNow.Ticks;
            if (Volatile.Read(ref _probePending) == 1)
            {
                long waited = now - Interlocked.Read(ref _probeSentTicks);
                if (waited >= FreezeThreshold && Interlocked.Exchange(ref _stackCapturedForProbe, 1) == 0)
                    Volatile.Write(ref _frozenStack, CaptureUiStack());
                if (
                    waited >= StuckThreshold
                    && Volatile.Read(ref _freezeReports) < MaxFreezeReports
                    && Interlocked.Exchange(ref _stuckReported, 1) == 0
                )
                {
                    App.Logger.WriteLine(
                        "UiFreeze",
                        "The window has not responded for "
                            + (waited / TimeSpan.TicksPerMillisecond)
                            + " ms and is still frozen"
                            + (_activeWindow.Length > 0 ? " in " + _activeWindow : "")
                            + DescribeCurrentWork()
                            + DescribeProcessState()
                            + Interlocked.Exchange(ref _frozenStack, "")
                    );
                    App.Logger.Flush();
                }
                return;
            }
            Interlocked.Exchange(ref _probeSentTicks, now);
            Volatile.Write(ref _probePending, 1);
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(OnProbeAnswered));
        }
        catch { }
    }

    private static string DescribeActiveWindow()
    {
        try
        {
            System.Windows.Application? application = System.Windows.Application.Current;
            if (application == null)
                return "";
            System.Windows.Window? window = null;
            foreach (System.Windows.Window candidate in application.Windows)
            {
                if (candidate.IsActive)
                {
                    window = candidate;
                    break;
                }
                if (window == null && candidate.IsVisible)
                    window = candidate;
            }
            if (window == null)
                return "";
            string description = window.GetType().Name;
            if (window is Voidstrap.UI.Elements.Settings.MainWindow main && main.RootFrame?.Content is object page)
                description += " on " + page.GetType().Name;
            return description;
        }
        catch
        {
            return "";
        }
    }

    private static string DescribeProcessState()
    {
        try
        {
            ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            using Process process = Process.GetCurrentProcess();
            return "\n  State: working set "
                + (process.WorkingSet64 / (1024 * 1024))
                + " MB, managed heap "
                + (GC.GetTotalMemory(false) / (1024 * 1024))
                + " MB, GC "
                + GC.CollectionCount(0)
                + "/"
                + GC.CollectionCount(1)
                + "/"
                + GC.CollectionCount(2)
                + ", last GC pause "
                + (gc.PauseDurations.Length > 0 ? (int)gc.PauseDurations[0].TotalMilliseconds : 0)
                + " ms, GC pause time "
                + gc.PauseTimePercentage.ToString("0.#", CultureInfo.InvariantCulture)
                + " percent, threads "
                + process.Threads.Count
                + ", thread pool busy "
                + (maxWorkers - availableWorkers)
                + ", queued work "
                + ThreadPool.PendingWorkItemCount
                + ", lock contention "
                + Monitor.LockContentionCount;
        }
        catch
        {
            return "";
        }
    }

    private static void CheckThreadPool()
    {
        long queued = ThreadPool.PendingWorkItemCount;
        if (queued < StarvationQueueLimit)
        {
            Interlocked.Exchange(ref _starvedTicks, 0);
            if (Interlocked.Exchange(ref _starvationReported, 0) == 1)
                App.Logger.WriteLine("ThreadPool", "Background work is flowing again, " + queued + " item(s) queued");
            return;
        }
        if (Interlocked.Increment(ref _starvedTicks) != StarvationTicksBeforeReport || Interlocked.Exchange(ref _starvationReported, 1) == 1)
            return;
        App.Logger.WriteLine(
            "ThreadPool",
            "Background work is backing up, "
                + queued
                + " item(s) queued for over "
                + (StarvationTicksBeforeReport * (int)WatchdogInterval.TotalMilliseconds)
                + " ms, something is blocking pool threads"
                + DescribeProcessState()
        );
    }

    private static void CheckResources()
    {
        try
        {
            if (Volatile.Read(ref _resourceReports) >= MaxResourceReports)
                return;
            using Process process = Process.GetCurrentProcess();
            int handles = process.HandleCount;
            int gdi = 0;
            int user = 0;
            if (OperatingSystem.IsWindows())
            {
                gdi = (int)GetGuiResources(process.Handle, 0);
                user = (int)GetGuiResources(process.Handle, 1);
            }
            long heap = GC.GetTotalMemory(false);
            if (Interlocked.CompareExchange(ref _baselineHeap, heap, 0) == 0)
                _nextHeapWarn = heap + HeapWarnStepBytes;
            _peakHandles = Math.Max(_peakHandles, handles);
            _peakGdi = Math.Max(_peakGdi, gdi);
            _peakUser = Math.Max(_peakUser, user);
            string? reason = null;
            if (handles >= _nextHandleWarn)
            {
                reason = "the process holds " + handles + " handles";
                _nextHandleWarn = handles + HandleWarnStep;
            }
            else if (gdi >= _nextGdiWarn)
            {
                reason = "the process holds " + gdi + " GDI objects, Windows stops drawing at 10000";
                _nextGdiWarn = gdi + GdiWarnStep;
            }
            else if (user >= _nextUserWarn)
            {
                reason = "the process holds " + user + " USER objects, Windows stops creating windows at 10000";
                _nextUserWarn = user + GdiWarnStep;
            }
            else if (_nextHeapWarn > 0 && heap >= _nextHeapWarn)
            {
                reason = "the managed heap grew to " + (heap / (1024 * 1024)) + " MB from " + (Interlocked.Read(ref _baselineHeap) / (1024 * 1024)) + " MB at the first check";
                _nextHeapWarn = heap + HeapWarnStepBytes;
            }
            if (reason == null)
                return;
            Interlocked.Increment(ref _resourceReports);
            App.Logger.WriteLine(
                "ResourceLeak",
                "Possible leak: "
                    + reason
                    + (OperatingSystem.IsWindows() ? ", GDI " + gdi + ", USER " + user : "")
                    + (_activeWindow.Length > 0 ? ", active window " + _activeWindow : "")
                    + DescribeProcessState()
            );
        }
        catch { }
    }

    private static string DescribeInner(Exception ex)
    {
        try
        {
            System.Text.StringBuilder builder = new();
            if (ex.HResult != 0 && ex.HResult != unchecked((int)0x80131500))
                builder.Append(" [0x").Append(ex.HResult.ToString("X8", CultureInfo.InvariantCulture)).Append(']');
            int depth = 0;
            for (Exception? inner = ex.InnerException; inner != null && depth < 4; inner = inner.InnerException, depth++)
                builder.Append(" caused by ").Append(inner.GetType().Name).Append(": ").Append(Trim(inner.Message));
            if (ex is AggregateException { InnerExceptions.Count: > 1 } aggregate)
                builder.Append(", ").Append(aggregate.InnerExceptions.Count).Append(" errors aggregated");
            return builder.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string CaptureUiStack()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.Increment(ref _stackCaptures) > MaxStackCaptures)
            return "";
#if !CROSSPLAT
        bool wasInside = _inside;
        _inside = true;
        string native = NativeStack.Capture();
        try
        {
            using Microsoft.Diagnostics.Runtime.DataTarget target = Microsoft.Diagnostics.Runtime.DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using Microsoft.Diagnostics.Runtime.ClrRuntime runtime = target.ClrVersions[0].CreateRuntime();
            Microsoft.Diagnostics.Runtime.ClrThread? thread = runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == _uiThreadId);
            if (thread == null)
                return native + "\n  UI thread stack: not found";
            System.Text.StringBuilder builder = new("\n  UI thread stack at the freeze:");
            int frames = 0;
            foreach (Microsoft.Diagnostics.Runtime.ClrStackFrame frame in thread.EnumerateStackTrace())
            {
                string? text = frame.Method?.Signature ?? frame.FrameName;
                if (string.IsNullOrEmpty(text))
                    continue;
                builder.Append("\n    at ").Append(text);
                if (++frames >= 60)
                {
                    builder.Append("\n    ...");
                    break;
                }
            }
            if (thread.CurrentException is { } exception)
                builder.Append("\n  UI thread is handling ").Append(exception.Type?.Name).Append(": ").Append(exception.Message);
            return native + builder.ToString();
        }
        catch (Exception ex)
        {
            return native + "\n  UI thread stack unavailable: " + ex.GetType().Name + " " + ex.Message;
        }
        finally
        {
            _inside = wasInside;
        }
#else
        return "";
#endif
    }

    private static void OnProbeAnswered()
    {
        long waited = DateTime.UtcNow.Ticks - Interlocked.Read(ref _probeSentTicks);
        string work = _lastSlowWork;
        _lastSlowWork = "";
        string frozenWindow = _activeWindow;
        _activeWindow = DescribeActiveWindow();
        string stack = Interlocked.Exchange(ref _frozenStack, "");
        Volatile.Write(ref _stackCapturedForProbe, 0);
        Volatile.Write(ref _probePending, 0);
        bool wasStuck = Interlocked.Exchange(ref _stuckReported, 0) == 1;
        if (waited < FreezeThreshold)
            return;
        if (waited > Interlocked.Read(ref _longestFreezeTicks))
        {
            Interlocked.Exchange(ref _longestFreezeTicks, waited);
            _longestFreezeWindow = frozenWindow;
        }
        if (Interlocked.Increment(ref _freezeReports) > MaxFreezeReports)
            return;
        App.Logger.WriteLine(
            "UiFreeze",
            "The window was frozen for "
                + (waited / TimeSpan.TicksPerMillisecond)
                + " ms"
                + (frozenWindow.Length > 0 ? " in " + frozenWindow : "")
                + work
                + (wasStuck ? " and has now recovered" : "")
                + DescribeProcessState()
                + stack
        );
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_inside)
            return;
        Exception ex = e.Exception;
        if (ex is OperationCanceledException || IsKnownInternalProbe(ex) || IsAbortedTransfer(ex))
            return;
        _inside = true;
        try
        {
            Interlocked.Increment(ref _total);
            string site = ex.GetType().Name + " at " + DescribeSite(ex);
            int count = Sites.AddOrUpdate(site, 1, static (_, previous) => previous + 1);
            if (count != 1)
                return;
            if (Sites.Count > MaxDistinctSites)
            {
                if (Interlocked.Exchange(ref _overflowReported, 1) == 0)
                    App.Logger.WriteLine(
                        LOG_IDENT,
                        "Over " + MaxDistinctSites + " different error sites this session, further new sites are counted but not described"
                    );
                return;
            }
            App.Logger.WriteLine(
                LOG_IDENT,
                site
                    + ": "
                    + Trim(ex.Message)
                    + DescribeInner(ex)
                    + (Environment.CurrentManagedThreadId == _uiThreadId ? " on the UI thread" : " on a background thread")
                    + CallerSummary()
            );
        }
        catch { }
        finally
        {
            _inside = false;
        }
    }

    private static bool IsAbortedTransfer(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.OperationAborted })
                return true;
        }
        return ex is System.IO.IOException
            && ex.Message.Contains("aborted because of either a thread exit or an application request", StringComparison.Ordinal);
    }

    private static bool IsKnownInternalProbe(Exception ex)
    {
        if (Integrations.Plugins.ModTranspiler.IsProbingJavaScript && ex is Acornima.ParseErrorException)
            return true;
        return ex.GetType().Name == "TypeNameParserException" && ex.TargetSite?.DeclaringType?.Namespace == "MS.Internal.Xaml.Parser";
    }

    private static string DescribeSite(Exception ex)
    {
        try
        {
            System.Reflection.MethodBase? method = ex.TargetSite;
            if (method == null)
                return "unknown";
            return (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string CallerSummary()
    {
        try
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            string callers = string.Join(
                " < ",
                frames
                    .Select(static frame => frame.GetMethod())
                    .Where(static method =>
                        method?.DeclaringType?.FullName is string name
                        && name.StartsWith("Voidstrap.", StringComparison.Ordinal)
                        && !name.StartsWith("Voidstrap.Utility.HiddenErrorLog", StringComparison.Ordinal)
                    )
                    .Take(3)
                    .Select(static method => method!.DeclaringType!.FullName + "." + method.Name)
            );
            return callers.Length == 0 ? "" : " (called from " + callers + ")";
        }
        catch
        {
            return "";
        }
    }

    private static string Trim(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return "(no message)";
        string flat = message.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length > MaxMessageLength ? flat[..MaxMessageLength] + "..." : flat;
    }

    private static void WriteSummary()
    {
        try
        {
            int freezes = Math.Min(Volatile.Read(ref _freezeReports), MaxFreezeReports);
            using (Process process = Process.GetCurrentProcess())
            {
                App.Logger.WriteLine(
                    LOG_IDENT,
                    "Session lasted "
                        + (int)(DateTime.UtcNow - SessionStarted).TotalMinutes
                        + " minute(s), peak memory "
                        + (process.PeakWorkingSet64 / (1024 * 1024))
                        + " MB, garbage collections "
                        + GC.CollectionCount(0)
                        + "/"
                        + GC.CollectionCount(1)
                        + "/"
                        + GC.CollectionCount(2)
                        + (
                            freezes > 0
                                ? ", the window froze for over 2 s "
                                    + freezes
                                    + " time(s), longest "
                                    + (Interlocked.Read(ref _longestFreezeTicks) / TimeSpan.TicksPerMillisecond)
                                    + " ms"
                                    + (_longestFreezeWindow.Length > 0 ? " in " + _longestFreezeWindow : "")
                                : ", no window froze"
                        )
                        + ", peak handles "
                        + _peakHandles
                        + (OperatingSystem.IsWindows() ? ", peak GDI " + _peakGdi + ", peak USER " + _peakUser : "")
                );
            }
            long total = Interlocked.Read(ref _total);
            if (total == 0)
                return;
            string top = string.Join(
                "; ",
                Sites.OrderByDescending(static pair => pair.Value).Take(10).Select(static pair => pair.Value + "x " + pair.Key)
            );
            App.Logger.WriteLine(LOG_IDENT, total + " handled error(s) at " + Sites.Count + " site(s) this session, most frequent: " + top);
        }
        catch { }
    }

    private sealed class DiagnosticListener(string category, bool onlyEvents) : TraceListener
    {
        private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!onlyEvents)
                Record(message);
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (eventType is TraceEventType.Critical or TraceEventType.Error or TraceEventType.Warning)
                Record(message);
        }

        public override void TraceEvent(
            TraceEventCache? eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string? format,
            params object?[]? args
        )
        {
            if (eventType is not (TraceEventType.Critical or TraceEventType.Error or TraceEventType.Warning) || format == null)
                return;
            string text;
            try
            {
                text = args == null || args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                text = format;
            }
            Record(text);
        }

        private void Record(string? message)
        {
            if (string.IsNullOrEmpty(message) || _inside)
                return;
            _inside = true;
            try
            {
                string text = Trim(HashCodePattern.Replace(message, ""));
                if (Seen.Count < MaxDistinctSites && Seen.TryAdd(category + text, 0))
                    App.Logger.WriteLine(category, text);
            }
            catch { }
            finally
            {
                _inside = false;
            }
        }
    }
}
