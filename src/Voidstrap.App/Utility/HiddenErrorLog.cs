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

internal static class HiddenErrorLog
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

	private static DispatcherOperation? _currentOperation;

	private static long _operationStartedTicks;

	private static string _lastSlowWork = "";

	[ThreadStatic]
	private static bool _inside;

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
			long now = DateTime.UtcNow.Ticks;
			if (Volatile.Read(ref _probePending) == 1)
			{
				long waited = now - Interlocked.Read(ref _probeSentTicks);
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
							+ DescribeCurrentWork()
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

	private static void OnProbeAnswered()
	{
		long waited = DateTime.UtcNow.Ticks - Interlocked.Read(ref _probeSentTicks);
		string work = _lastSlowWork;
		_lastSlowWork = "";
		Volatile.Write(ref _probePending, 0);
		bool wasStuck = Interlocked.Exchange(ref _stuckReported, 0) == 1;
		if (waited < FreezeThreshold)
			return;
		if (waited > Interlocked.Read(ref _longestFreezeTicks))
			Interlocked.Exchange(ref _longestFreezeTicks, waited);
		if (Interlocked.Increment(ref _freezeReports) > MaxFreezeReports)
			return;
		App.Logger.WriteLine(
			"UiFreeze",
			"The window was frozen for "
				+ (waited / TimeSpan.TicksPerMillisecond)
				+ " ms"
				+ work
				+ (wasStuck ? " and has now recovered" : "")
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
			App.Logger.WriteLine(LOG_IDENT, site + ": " + Trim(ex.Message) + CallerSummary());
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
						+ ", window freezes over 2 s: "
						+ freezes
						+ (
							freezes > 0
								? ", longest " + (Interlocked.Read(ref _longestFreezeTicks) / TimeSpan.TicksPerMillisecond) + " ms"
								: ""
						)
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
