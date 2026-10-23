using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class RequestErrorLog
{
	private const string LOG_IDENT = "RequestErrors";

	private const int MaxDistinctFailures = 200;

	private const string HttpListenerName = "HttpHandlerDiagnosticListener";

	private const string StopEvent = "System.Net.Http.HttpRequestOut.Stop";

	private const string ExceptionEvent = "System.Net.Http.Exception";

	private static readonly Regex IdSegment = new(@"(?<=/)[^/]*\d{3,}[^/]*(?=/|$)", RegexOptions.Compiled);

	private static readonly ConcurrentDictionary<string, int> Failures = new(StringComparer.Ordinal);

	private static readonly List<IDisposable> Subscriptions = new();

	private static readonly object Sync = new();

	private static int _installed;

	public static void Install()
	{
		if (Interlocked.Exchange(ref _installed, 1) == 1)
			return;
		try
		{
			lock (Sync)
				Subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(new ListenerObserver()));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Request error reporting could not start: " + ex.Message);
		}
	}

	public static void Uninstall()
	{
		if (Interlocked.Exchange(ref _installed, 0) == 0)
			return;
		lock (Sync)
		{
			foreach (IDisposable subscription in Subscriptions)
			{
				try
				{
					subscription.Dispose();
				}
				catch
				{
				}
			}
			Subscriptions.Clear();
		}
		WriteSummary();
	}

	private static void OnEvent(KeyValuePair<string, object?> pair)
	{
		try
		{
			if (pair.Key == StopEvent)
			{
				if (Read<TaskStatus?>(pair.Value, "RequestTaskStatus") == TaskStatus.Canceled)
					return;
				HttpResponseMessage? response = Read<HttpResponseMessage>(pair.Value, "Response");
				HttpRequestMessage? request = Read<HttpRequestMessage>(pair.Value, "Request");
				if (response == null || request == null || (int)response.StatusCode < 400)
					return;
				Record(request, "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);
			}
			else if (pair.Key == ExceptionEvent)
			{
				Exception? exception = Read<Exception>(pair.Value, "Exception");
				HttpRequestMessage? request = Read<HttpRequestMessage>(pair.Value, "Request");
				if (exception == null || request == null || exception is OperationCanceledException)
					return;
				Record(request, exception.GetType().Name + ": " + Flatten(exception.GetBaseException().Message));
			}
		}
		catch
		{
		}
	}

	private static void Record(HttpRequestMessage request, string problem)
	{
		string key = request.Method.Method + " " + Describe(request.RequestUri) + " returned " + problem;
		int count = Failures.AddOrUpdate(key, 1, static (_, previous) => previous + 1);
		if (count != 1 || Failures.Count > MaxDistinctFailures)
			return;
		double? seconds = Activity.Current?.Duration.TotalSeconds;
		App.Logger.WriteLine(LOG_IDENT, key + (seconds is > 0 ? " after " + seconds.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s" : ""));
	}

	private static string Describe(Uri? uri)
	{
		if (uri == null)
			return "(unknown address)";
		if (!uri.IsAbsoluteUri)
			return IdSegment.Replace(uri.OriginalString.Split('?')[0], "{id}");
		return uri.Host + IdSegment.Replace(uri.AbsolutePath, "{id}");
	}

	private static string Flatten(string message)
	{
		string flat = message.Replace('\r', ' ').Replace('\n', ' ');
		return flat.Length > 200 ? flat[..200] + "..." : flat;
	}

	private static T? Read<T>(object? payload, string property)
	{
		if (payload == null)
			return default;
		object? value = payload.GetType().GetProperty(property)?.GetValue(payload);
		return value is T typed ? typed : default;
	}

	private static void WriteSummary()
	{
		try
		{
			if (Failures.IsEmpty)
				return;
			string top = string.Join("; ", Failures.OrderByDescending(static pair => pair.Value).Take(10).Select(static pair => pair.Value + "x " + pair.Key));
			App.Logger.WriteLine(LOG_IDENT, Failures.Values.Sum() + " failed request(s) to " + Failures.Count + " endpoint(s) this session, most frequent: " + top);
		}
		catch
		{
		}
	}

	private sealed class ListenerObserver : IObserver<DiagnosticListener>
	{
		public void OnNext(DiagnosticListener listener)
		{
			if (listener.Name != HttpListenerName)
				return;
			IDisposable subscription = listener.Subscribe(new EventObserver(), static (name, _, _) => name is StopEvent or ExceptionEvent || name == "System.Net.Http.HttpRequestOut");
			lock (Sync)
				Subscriptions.Add(subscription);
		}

		public void OnError(Exception error)
		{
		}

		public void OnCompleted()
		{
		}
	}

	private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
	{
		public void OnNext(KeyValuePair<string, object?> value)
		{
			OnEvent(value);
		}

		public void OnError(Exception error)
		{
		}

		public void OnCompleted()
		{
		}
	}
}
