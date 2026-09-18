using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Voidstrap.Utility;

public sealed class AppNotification
{
	public string Id { get; set; } = "";

	public string Kind { get; set; } = "";

	public string Key { get; set; } = "";

	public string Title { get; set; } = "";

	public string Text { get; set; } = "";

	public string Source { get; set; } = "";

	public string LogPath { get; set; } = "";

	public int Count { get; set; } = 1;

	public long FirstSeen { get; set; }

	public long LastSeen { get; set; }

	public bool Read { get; set; }
}

public static class AppNotifications
{
	public const string KindCrash = "crash";

	public const string KindError = "error";

	public const string KindInfo = "info";

	private const string LOG_IDENT = "AppNotifications";

	private const string OverflowKey = "overflow";

	private const int MaxEntries = 50;

	private const int MaxNewErrorsPerHour = 10;

	private const int MaxTextLength = 400;

	private const long MaxFileBytes = 4194304;

	private static readonly TimeSpan ResurfaceAfter = TimeSpan.FromHours(24);

	private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(2);

	private static readonly object Sync = new();

	private static readonly Dictionary<string, AppNotification> Pending = new(StringComparer.Ordinal);

	private static readonly Queue<long> NewErrorTimes = new();

	private static IReadOnlyList<AppNotification> _items = [];

	private static DateTime _loadedStampUtc = DateTime.MinValue;

	private static bool _loaded;

	private static Timer? _flushTimer;

	[ThreadStatic]
	private static bool _busy;

	public static event Action? Changed;

	private static string FilePath => Path.Combine(Paths.Data, "Notifications.json");

	public static IReadOnlyList<AppNotification> Items
	{
		get
		{
			lock (Sync)
			{
				return _items;
			}
		}
	}

	public static int UnreadCount
	{
		get
		{
			lock (Sync)
			{
				return _items.Count(item => !item.Read);
			}
		}
	}

	public static void RecordError(string source, Exception? ex)
	{
		if (ex == null || _busy || !Paths.Initialized || IsNoise(ex))
			return;
		_busy = true;
		try
		{
			Exception root = Unwrap(ex);
			string key = BuildKey(root);
			bool allowed;
			lock (Sync)
			{
				allowed = Pending.ContainsKey(key) || _items.Any(item => item.Key == key) || TakeNewErrorSlot();
			}
			if (allowed)
				Stage(key, KindError, "Something went wrong in " + Area(source), Describe(root), source, false);
			else
				Stage(OverflowKey, KindError, "More errors were logged", "Voidstrap logged more errors than this inbox shows. Open the log for the full details.", source, false);
		}
		catch
		{
		}
		finally
		{
			_busy = false;
		}
	}

	public static void RecordCrash(string source, Exception? ex)
	{
		if (ex == null || _busy || !Paths.Initialized)
			return;
		_busy = true;
		try
		{
			Exception root = Unwrap(ex);
			Stage(BuildKey(root), KindCrash, "Voidstrap crashed", Describe(root), source, true);
		}
		catch
		{
		}
		finally
		{
			_busy = false;
		}
	}

	public static void RecordInfo(string key, string title, string text)
	{
		if (string.IsNullOrWhiteSpace(key) || _busy || !Paths.Initialized)
			return;
		_busy = true;
		try
		{
			Stage("info:" + key, KindInfo, title, text, "", false);
		}
		catch
		{
		}
		finally
		{
			_busy = false;
		}
	}

	public static bool Reload()
	{
		if (!Paths.Initialized)
			return false;
		DateTime stamp = GetStamp();
		lock (Sync)
		{
			if (_loaded && stamp == _loadedStampUtc)
				return false;
		}
		List<AppNotification> items = ReadFile();
		lock (Sync)
		{
			_items = items;
			_loadedStampUtc = stamp;
			_loaded = true;
		}
		RaiseChanged();
		return true;
	}

	public static void MarkAllRead()
	{
		Update(items =>
		{
			bool changed = false;
			foreach (AppNotification item in items.Where(item => !item.Read))
			{
				item.Read = true;
				changed = true;
			}
			return changed;
		});
	}

	public static void MarkRead(string id)
	{
		Update(items =>
		{
			AppNotification? item = items.FirstOrDefault(value => value.Id == id);
			if (item == null || item.Read)
				return false;
			item.Read = true;
			return true;
		});
	}

	public static void Clear()
	{
		Update(items =>
		{
			if (items.Count == 0)
				return false;
			items.Clear();
			return true;
		});
	}

	public static void Flush()
	{
		List<AppNotification> batch;
		lock (Sync)
		{
			if (Pending.Count == 0)
				return;
			batch = Pending.Values.ToList();
			Pending.Clear();
		}
		Update(items => Merge(items, batch));
	}

	public static void Shutdown()
	{
		Timer? timer;
		lock (Sync)
		{
			timer = _flushTimer;
			_flushTimer = null;
		}
		timer?.Dispose();
		Flush();
	}

	private static void Stage(string key, string kind, string title, string text, string source, bool flushNow)
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string logPath = App.Logger.FileLocation ?? "";
		lock (Sync)
		{
			if (Pending.TryGetValue(key, out AppNotification? pending))
			{
				pending.Count++;
				pending.LastSeen = now;
				pending.Text = text;
				pending.Source = source;
				pending.LogPath = logPath;
				if (kind == KindCrash)
				{
					pending.Kind = KindCrash;
					pending.Title = title;
				}
			}
			else
			{
				Pending[key] = new AppNotification
				{
					Id = Guid.NewGuid().ToString("N"),
					Kind = kind,
					Key = key,
					Title = title,
					Text = text,
					Source = source,
					LogPath = logPath,
					FirstSeen = now,
					LastSeen = now
				};
			}
			if (!flushNow)
			{
				_flushTimer ??= new Timer(OnFlushTimer);
				_flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
			}
		}
		if (flushNow)
			Flush();
	}

	private static void OnFlushTimer(object? state)
	{
		try
		{
			Flush();
		}
		catch
		{
		}
	}

	private static bool Merge(List<AppNotification> items, List<AppNotification> batch)
	{
		foreach (AppNotification incoming in batch)
		{
			AppNotification? current = items.FirstOrDefault(item => item.Key == incoming.Key);
			if (current == null)
			{
				items.Add(incoming);
				continue;
			}
			bool resurface = incoming.LastSeen - current.LastSeen > ResurfaceAfter.TotalMilliseconds;
			current.Count = (int)Math.Min((long)current.Count + incoming.Count, int.MaxValue);
			current.LastSeen = Math.Max(current.LastSeen, incoming.LastSeen);
			current.Text = incoming.Text;
			current.Source = incoming.Source;
			current.LogPath = incoming.LogPath;
			if (incoming.Kind == KindCrash && current.Kind != KindCrash)
			{
				current.Kind = KindCrash;
				current.Title = incoming.Title;
				resurface = true;
			}
			if (resurface)
				current.Read = false;
		}
		items.Sort((left, right) => right.LastSeen.CompareTo(left.LastSeen));
		while (items.Count > MaxEntries)
		{
			int index = items.FindLastIndex(item => item.Read);
			items.RemoveAt(index >= 0 ? index : items.Count - 1);
		}
		return true;
	}

	private static void Update(Func<List<AppNotification>, bool> change)
	{
		if (!Paths.Initialized)
			return;
		bool wasBusy = _busy;
		_busy = true;
		bool changed = false;
		try
		{
			using InterProcessLock gate = new InterProcessLock("Notifications", TimeSpan.FromSeconds(3));
			List<AppNotification> items = ReadFile();
			changed = change(items);
			if (changed)
			{
				Directory.CreateDirectory(Paths.Data);
				JsonFile.SerializeAtomic(FilePath, items, createBackup: false);
			}
			DateTime stamp = GetStamp();
			lock (Sync)
			{
				_items = items;
				_loadedStampUtc = stamp;
				_loaded = true;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Could not update the notification inbox: " + ex.Message);
		}
		finally
		{
			_busy = wasBusy;
		}
		if (changed)
			RaiseChanged();
	}

	private static List<AppNotification> ReadFile()
	{
		try
		{
			if (!File.Exists(FilePath))
				return [];
			List<AppNotification>? items = JsonFile.Deserialize<List<AppNotification>>(FilePath, JsonOptions.Tolerant, MaxFileBytes);
			return items?.Where(item => item != null && !string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(item.Key)).Take(MaxEntries).ToList() ?? [];
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Could not read the notification inbox: " + ex.Message);
			return [];
		}
	}

	private static DateTime GetStamp()
	{
		try
		{
			return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
		}
		catch
		{
			return DateTime.MinValue;
		}
	}

	private static void RaiseChanged()
	{
		Action? handlers = Changed;
		if (handlers == null)
			return;
		foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
		{
			try
			{
				handler();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "A notification listener failed: " + ex.Message);
			}
		}
	}

	private static bool TakeNewErrorSlot()
	{
		long now = Environment.TickCount64;
		while (NewErrorTimes.Count > 0 && now - NewErrorTimes.Peek() > 3600000)
			NewErrorTimes.Dequeue();
		if (NewErrorTimes.Count >= MaxNewErrorsPerHour)
			return false;
		NewErrorTimes.Enqueue(now);
		return true;
	}

	private static bool IsNoise(Exception ex)
	{
		if (App.LaunchSettings?.WindowAuditFlag.Active == true)
			return true;
		for (Exception? current = ex; current != null; current = current.InnerException)
		{
			if (current is OperationCanceledException)
				return true;
		}
		if (ex is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(inner => inner is OperationCanceledException))
			return true;
		return Connectivity.IsConnectionFailure(ex);
	}

	private static Exception Unwrap(Exception ex)
	{
		for (int depth = 0; depth < 8; depth++)
		{
			if (ex is AggregateException { InnerException: Exception aggregateInner })
				ex = aggregateInner;
			else if (ex is System.Reflection.TargetInvocationException { InnerException: Exception invocationInner })
				ex = invocationInner;
			else
				break;
		}
		return ex;
	}

	private static string Area(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
			return "Voidstrap";
		int separator = source.IndexOf("::", StringComparison.Ordinal);
		return separator > 0 ? source.Substring(0, separator) : source;
	}

	private static string Describe(Exception ex)
	{
		string message = FirstLine(ex.Message);
		string text = string.IsNullOrEmpty(message) ? ex.GetType().Name : ex.GetType().Name + ": " + message;
		return text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) : text;
	}

	private static string BuildKey(Exception ex)
	{
		string frame = FirstLine(ex.StackTrace ?? "").Trim();
		int open = frame.IndexOf('(');
		if (open > 0)
			frame = frame.Substring(0, open);
		return ex.GetType().FullName + "|" + frame + "|" + WithoutDigits(FirstLine(ex.Message));
	}

	private static string FirstLine(string value)
	{
		if (string.IsNullOrEmpty(value))
			return "";
		int end = value.IndexOfAny(new[] { '\r', '\n' });
		return (end >= 0 ? value.Substring(0, end) : value).Trim();
	}

	private static string WithoutDigits(string value)
	{
		StringBuilder builder = new StringBuilder(Math.Min(value.Length, 200));
		foreach (char character in value)
		{
			if (builder.Length >= 200)
				break;
			if (char.IsDigit(character))
			{
				if (builder.Length == 0 || builder[^1] != '#')
					builder.Append('#');
			}
			else
			{
				builder.Append(character);
			}
		}
		return builder.ToString();
	}
}
