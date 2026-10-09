using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal sealed class MacVirtualDisplaySession : IDisposable
{
	private const string LogIdent = "VirtualDisplay";
	private static readonly SemaphoreSlim SessionSlot = new(1, 1);
	private readonly object _gate = new();
	private readonly CancellationTokenSource _cancellation = new();
	private Process? _process;
	private bool _disposed;
	private bool _started;

	public void Start()
	{
		lock (_gate)
		{
			if (_disposed || _started || !Platform.IsMacOS)
				return;
			_started = true;
			_ = StartAsync();
		}
	}

	private async Task StartAsync()
	{
		bool acquired = false;
		bool monitoring = false;
		try
		{
			await SessionSlot.WaitAsync(_cancellation.Token).ConfigureAwait(false);
			acquired = true;
			lock (_gate)
			{
				if (_disposed || _process != null || !Platform.IsMacOS)
					return;
				string executable = Path.Combine(AppContext.BaseDirectory, "voidstrap-virtualdisplay");
				if (!File.Exists(executable))
				{
					App.Logger.WriteLine(LogIdent, "The virtual display helper is missing, reinstall the macOS package");
					return;
				}
				ProcessStartInfo info = new(executable)
				{
					UseShellExecute = false,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};
				info.ArgumentList.Add("--watch-stdin");
				_process = Process.Start(info);
				if (_process != null)
				{
					_cancellation.CancelAfter(TimeSpan.FromSeconds(15));
					monitoring = true;
					_ = ObserveAsync(_process, _cancellation.Token);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The virtual display could not start: " + ex.Message);
		}
		finally
		{
			if (acquired && !monitoring)
				SessionSlot.Release();
		}
	}

	private async Task ReadOutputAsync(StreamReader reader, CancellationToken token, bool status)
	{
		while (!token.IsCancellationRequested)
		{
			string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
			if (line == null)
				return;
			if (status && line.StartsWith("{\"ready\":true,", StringComparison.Ordinal))
			{
				lock (_gate)
				{
					if (!_disposed)
						_cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
				}
			}
			App.Logger.WriteLine(LogIdent, line);
		}
	}

	private async Task ObserveAsync(Process process, CancellationToken token)
	{
		try
		{
			await Task.WhenAll(ReadOutputAsync(process.StandardOutput, token, true), ReadOutputAsync(process.StandardError, token, false), process.WaitForExitAsync(token)).ConfigureAwait(false);
			if (process.ExitCode != 0)
				App.Logger.WriteLine(LogIdent, "The virtual display stopped with exit code " + process.ExitCode);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (!process.HasExited)
				{
					App.Logger.WriteLine(LogIdent, "The virtual display timed out, closing its process");
					process.Kill();
					await process.WaitForExitAsync().ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "The virtual display process could not be closed: " + ex.Message);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The virtual display monitor stopped: " + ex.Message);
		}
		finally
		{
			lock (_gate)
			{
				_process = null;
				process.Dispose();
				SessionSlot.Release();
				if (_disposed)
					_cancellation.Dispose();
			}
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			try
			{
				_process?.StandardInput.Close();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "The virtual display shutdown request failed: " + ex.Message);
			}
			if (_process != null)
				_cancellation.CancelAfter(TimeSpan.FromSeconds(10));
			if (_process == null)
			{
				_cancellation.Cancel();
				_cancellation.Dispose();
			}
		}
		GC.SuppressFinalize(this);
	}
}
