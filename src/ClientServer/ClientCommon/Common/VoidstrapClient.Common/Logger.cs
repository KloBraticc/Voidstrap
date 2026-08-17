using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.Common;

public class Logger : IDisposable
{
	private const long MaxLogBytes = 16777216;
	private const int MaxMessageLength = 8192;
	private const int MaxLogFiles = 10;
	public static readonly Logger Instance;

	private readonly StreamWriter _Writer;

	private readonly object _writeLock = new object();

	private bool _Verbose;

	private long _bytesWritten;

	private bool _disposed;

	private static string ConstructLogFileName()
	{
		return (Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown") + "_" + DateTime.UtcNow.ToString("O").Replace("-", "").Replace(":", "")
			.Replace(".", "") + ".log";
	}

	private Logger(bool verbose)
	{
		string path = ConstructLogFileName();
		string path2 = Path.Combine(PathHelper.Logs, path);
		_Writer = new StreamWriter(path2);
		_Writer.AutoFlush = true;
		_bytesWritten = _Writer.BaseStream.Length;
		_Verbose = verbose;
	}

	private string ConstructLogOutput(LogType type, string message)
	{
		return $"{DateTime.UtcNow.ToString("O")} [{type}] {message}";
	}

	private void Log(LogType type, string message)
	{
		if (message.Length > MaxMessageLength)
			message = message.Substring(0, MaxMessageLength);
		string value = ConstructLogOutput(type, message);
		lock (_writeLock)
		{
			try
			{
				Console.WriteLine(value);
			}
			catch (IOException)
			{
			}
			if (!_disposed)
			{
				long bytes = Encoding.UTF8.GetByteCount(value) + Environment.NewLine.Length;
				if (_bytesWritten + bytes <= MaxLogBytes)
				{
					try
					{
						_Writer.WriteLine(value);
						_bytesWritten += bytes;
					}
					catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ObjectDisposedException)
					{
						_disposed = true;
						try
						{
							_Writer.Dispose();
						}
						catch
						{
						}
					}
				}
			}
		}
	}

	private void OnProcessExit(object? sender, EventArgs e)
	{
		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		Dispose();
	}

	public void Dispose()
	{
		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		lock (_writeLock)
		{
			if (_disposed)
				return;
			_disposed = true;
			try
			{
				_Writer.Dispose();
			}
			catch
			{
			}
		}
		GC.SuppressFinalize(this);
	}

	[Conditional("DEBUG")]
	public void Verbose(string message)
	{
		if (_Verbose)
		{
			Log(LogType.Verbose, message);
		}
	}

	[Conditional("DEBUG")]
	public void Debug(string message)
	{
		Log(LogType.Verbose, message);
	}

	public void Info(string message)
	{
		Log(LogType.Information, message);
	}

	public void Warn(string message)
	{
		Log(LogType.Warning, message);
	}

	public void Error(string message)
	{
		Log(LogType.Error, message);
	}

	private static void CleanupOldLogs()
	{
		FileInfo[] files;
		try
		{
			if (!Directory.Exists(PathHelper.Logs))
			{
				return;
			}
			files = new DirectoryInfo(PathHelper.Logs).GetFiles("*.log");
		}
		catch
		{
			return;
		}

		if (files.Length <= MaxLogFiles)
		{
			return;
		}

		Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

		for (int i = MaxLogFiles; i < files.Length; i++)
		{
			try
			{
				files[i].Delete();
			}
			catch
			{
			}
		}
	}

	static Logger()
	{
		try
		{
			Directory.CreateDirectory(PathHelper.Logs);
			CleanupOldLogs();
		}
		catch
		{
		}
		Instance = new Logger(verbose: false);
		AppDomain.CurrentDomain.ProcessExit += Instance.OnProcessExit;
	}
}
