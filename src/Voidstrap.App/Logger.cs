using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using Voidstrap.Resources;
using Voidstrap.UI;

namespace Voidstrap;

public class Logger : IDisposable
{
	private const int MaxHistoryEntries = 150;

	private const int MaxEntryCharacters = 32768;

	private const int MaxLogFiles = 10;

	private readonly Channel<string> _writeQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
	{
		SingleReader = true,
		SingleWriter = false,
		FullMode = BoundedChannelFullMode.DropOldest
	});

	private readonly CancellationTokenSource _writerCts = new CancellationTokenSource();

	private StreamWriter? _writer;

	private Task? _writerTask;

	private readonly object _historyLock = new object();

	private string _lastCategory = "";

	private bool _disposed;

	public List<string> History { get; } = new List<string>();

	public bool Initialized { get; private set; }

	public bool NoWriteMode { get; private set; }

	public string? FileLocation { get; private set; }

	public string AsDocument
	{
		get
		{
			lock (_historyLock)
			{
				return string.Join('\n', History);
			}
		}
	}

	public void Initialize(bool useTempDir = false)
	{
		if (Initialized || _disposed)
		{
			WriteLine("Logger::Initialize", "Logger is already initialized");
			return;
		}
		string text = (useTempDir ? Path.Combine(Paths.TempLogs) : Paths.Logs);
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Paths.TempLogs;
		}
		string text2 = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
		string path = "Voidstrap_" + text2 + "_" + Environment.ProcessId + ".log";
		string text3 = Path.Combine(text, path);
		try
		{
			Directory.CreateDirectory(text);
			FileStream stream = new FileStream(text3, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
			StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096);
			lock (_historyLock)
			{
				_writer = writer;
				FileLocation = text3;
				string history = string.Join("\r\n", History);
				if (history.Length > 0)
				{
					_writeQueue.Writer.TryWrite(history);
				}
				Initialized = true;
				_writerTask = Task.Run(() => ProcessWritesAsync(writer));
			}
			WriteLine("Logger::Initialize", "Logger initialized at " + text3);
			CleanupOldLogs(text, text3);
		}
		catch (UnauthorizedAccessException)
		{
			if (!NoWriteMode)
			{
				WriteLine("Logger::Initialize", "No write access to " + text);
				Frontend.ShowMessageBox(string.Format(Strings.Logger_NoWriteMode, text), MessageBoxImage.Exclamation);
				NoWriteMode = true;
			}
		}
		catch (IOException ex2)
		{
			WriteLine("Logger::Initialize", "Failed to initialize due to IO exception: " + ex2.Message);
		}
		catch (Exception ex3)
		{
			WriteLine("Logger::Initialize", "Failed to initialize at " + text + ": " + ex3.Message);
		}
	}

	private void CleanupOldLogs(string directory, string? currentLog)
	{
		FileInfo[] files;
		try
		{
			if (!Directory.Exists(directory))
			{
				return;
			}
			files = new DirectoryInfo(directory).GetFiles("Voidstrap_*.log");
		}
		catch (Exception ex)
		{
			WriteLine("Logger::Cleanup", "Could not read the log folder: " + ex.Message);
			return;
		}

		if (files.Length <= MaxLogFiles)
		{
			return;
		}

		Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

		int kept = 0;
		int removed = 0;
		int locked = 0;
		foreach (FileInfo file in files)
		{
			bool isCurrent = currentLog != null && string.Equals(file.FullName, currentLog, StringComparison.OrdinalIgnoreCase);
			if (isCurrent || kept < MaxLogFiles)
			{
				kept++;
				continue;
			}
			try
			{
				file.Delete();
				removed++;
			}
			catch (IOException)
			{
				locked++;
			}
			catch (UnauthorizedAccessException)
			{
				locked++;
			}
		}

		if (removed > 0 || locked > 0)
		{
			WriteLine("Logger::Cleanup", "Kept the newest " + MaxLogFiles + " logs, removed " + removed + ", still in use " + locked);
		}
	}

	private static string GetCategory(string identifier)
	{
		int num = identifier.IndexOf("::", StringComparison.Ordinal);
		if (num <= 0)
		{
			return identifier;
		}
		return identifier.Substring(0, num);
	}

	private void WriteEntry(string category, string body)
	{
		string text = DateTime.UtcNow.ToString("s") + "Z";
		string boundedBody = body.Length > MaxEntryCharacters ? body.Substring(0, MaxEntryCharacters) : body;
		string text2 = string.IsNullOrEmpty(Paths.UserProfile) ? boundedBody : boundedBody.Replace(Paths.UserProfile, "%UserProfile%", StringComparison.InvariantCultureIgnoreCase);
		string text3 = text + " " + text2;
		bool flag;
		lock (_historyLock)
		{
			if (_disposed)
			{
				return;
			}
			flag = _lastCategory.Length > 0 && !string.Equals(category, _lastCategory, StringComparison.Ordinal);
			_lastCategory = category;
			if (flag)
			{
				History.Add(string.Empty);
			}
			History.Add(text3);
			while (History.Count > MaxHistoryEntries)
			{
				History.RemoveAt(0);
			}
			if (flag)
			{
				QueueWrite(string.Empty);
			}
			QueueWrite(text3);
		}
	}

	public void WriteLine(string identifier, string message)
	{
		WriteEntry(GetCategory(identifier), "[" + identifier + "] " + message);
	}

	public void WriteException(string identifier, Exception ex)
	{
		string value = $"0x{ex.HResult:X8}";
		WriteEntry(GetCategory(identifier), $"[{identifier}] ({value}) {ex}");
	}

	private void QueueWrite(string message)
	{
		if (!Initialized || NoWriteMode || _disposed || _writer == null)
		{
			return;
		}
		_writeQueue.Writer.TryWrite(message);
	}

	private async Task ProcessWritesAsync(StreamWriter writer)
	{
		try
		{
			while (await _writeQueue.Reader.WaitToReadAsync(_writerCts.Token).ConfigureAwait(continueOnCapturedContext: false))
			{
				while (_writeQueue.Reader.TryRead(out string? message))
				{
					await writer.WriteLineAsync(message).ConfigureAwait(continueOnCapturedContext: false);
				}
				await writer.FlushAsync(_writerCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
		catch (IOException)
		{
			lock (_historyLock)
			{
				NoWriteMode = true;
				Initialized = false;
				_writer = null;
				while (_writeQueue.Reader.TryRead(out _))
				{
				}
			}
		}
		finally
		{
			writer.Dispose();
		}
	}

	public void Dispose()
	{
		Task? writerTask;
		StreamWriter? writer;
		lock (_historyLock)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			Initialized = false;
			_writeQueue.Writer.TryComplete();
			writerTask = _writerTask;
			writer = _writer;
			_writerTask = null;
			_writer = null;
		}
		bool writerStopped = writerTask == null;
		try
		{
			if (writerTask != null)
			{
				writerStopped = writerTask.Wait(TimeSpan.FromSeconds(2));
				if (!writerStopped)
				{
					_writerCts.Cancel();
					writerStopped = writerTask.Wait(TimeSpan.FromSeconds(3));
				}
			}
		}
		catch
		{
		}
		if (writerStopped)
		{
			_writerCts.Dispose();
			writer?.Dispose();
		}
		else if (writerTask != null)
		{
			_ = writerTask.ContinueWith(_ =>
			{
				writer?.Dispose();
				_writerCts.Dispose();
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}
		GC.SuppressFinalize(this);
	}
}
