using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Voidstrap.Utility;

internal static class HiddenErrorLog
{
	private const string LOG_IDENT = "HiddenErrors";

	private const int MaxDistinctSites = 300;

	private const int MaxMessageLength = 300;

	private static readonly ConcurrentDictionary<string, int> Sites = new(StringComparer.Ordinal);

	private static int _installed;

	private static int _overflowReported;

	private static long _total;

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
			PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
			PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Binding error reporting could not start: " + ex.Message);
		}
	}

	public static void Uninstall()
	{
		if (Interlocked.Exchange(ref _installed, 0) == 0)
			return;
		AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
		WriteSummary();
	}

	private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
	{
		if (_inside)
			return;
		Exception ex = e.Exception;
		if (ex is OperationCanceledException)
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
					App.Logger.WriteLine(LOG_IDENT, "Over " + MaxDistinctSites + " different error sites this session, further new sites are counted but not described");
				return;
			}
			App.Logger.WriteLine(LOG_IDENT, site + ": " + Trim(ex.Message) + CallerSummary());
		}
		catch
		{
		}
		finally
		{
			_inside = false;
		}
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
			string callers = string.Join(" < ", frames
				.Select(static frame => frame.GetMethod())
				.Where(static method => method?.DeclaringType?.FullName is string name && name.StartsWith("Voidstrap.", StringComparison.Ordinal) && !name.StartsWith("Voidstrap.Utility.HiddenErrorLog", StringComparison.Ordinal))
				.Take(3)
				.Select(static method => method!.DeclaringType!.FullName + "." + method.Name));
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
			long total = Interlocked.Read(ref _total);
			if (total == 0)
				return;
			string top = string.Join("; ", Sites.OrderByDescending(static pair => pair.Value).Take(10).Select(static pair => pair.Value + "x " + pair.Key));
			App.Logger.WriteLine(LOG_IDENT, total + " handled error(s) at " + Sites.Count + " site(s) this session, most frequent: " + top);
		}
		catch
		{
		}
	}

	private sealed class BindingErrorListener : TraceListener
	{
		private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

		public override void Write(string? message)
		{
		}

		public override void WriteLine(string? message)
		{
			if (string.IsNullOrEmpty(message) || _inside)
				return;
			_inside = true;
			try
			{
				string text = Trim(System.Text.RegularExpressions.Regex.Replace(message, @"\s*\(HashCode=\d+\)", ""));
				if (Seen.Count < MaxDistinctSites && Seen.TryAdd(text, 0))
					App.Logger.WriteLine("BindingErrors", text);
			}
			catch
			{
			}
			finally
			{
				_inside = false;
			}
		}
	}
}
