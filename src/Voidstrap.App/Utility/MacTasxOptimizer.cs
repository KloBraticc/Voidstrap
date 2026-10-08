using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Persistable;

namespace Voidstrap.Utility;

internal sealed class MacTasxOptimizer : IDisposable
{
	private const string LogIdent = "MacTasxOptimizer";

	private const int TickIntervalMs = 500;

	private const int ScanEveryTicks = 4;

	private readonly CancellationTokenSource _cancellation = new();

	private readonly Dictionary<int, bool> _throttled = [];

	private Task? _loop;

	private int _started;

	private bool _disposed;

	public static bool ShouldRun(AppSettings? settings) => Platform.IsMacOS && settings != null && settings.TasxOptimization;

	public void Start()
	{
		if (_disposed || !OperatingSystem.IsMacOS() || Interlocked.Exchange(ref _started, 1) != 0)
			return;
		App.Logger.WriteLine(LogIdent, "TASX optimization started");
		_loop = Task.Run(() => RunAsync(_cancellation.Token));
	}

	private async Task RunAsync(CancellationToken token)
	{
		int tick = 0;
		List<int> games = [];
		try
		{
			while (!token.IsCancellationRequested)
			{
				if (tick % ScanEveryTicks == 0)
				{
					games = [.. MacRobloxProcesses.Scan().GamePids];
					Forget(games);
				}
				Apply(games, Voidstrap.Platform.MacOS.MacOSProcessPolicy.FrontmostProcessId());
				tick++;
				await Task.Delay(TickIntervalMs, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Optimizer loop failed: " + ex.Message);
		}
		finally
		{
			ReleaseAll();
		}
	}

	private void Apply(List<int> games, int frontmost)
	{
		if (frontmost <= 0)
			return;
		foreach (int pid in games)
		{
			bool background = pid != frontmost;
			if (_throttled.TryGetValue(pid, out bool current) && current == background)
				continue;
			if (!Voidstrap.Platform.MacOS.MacOSProcessPolicy.SetBackground(pid, background))
			{
				_throttled[pid] = background;
				continue;
			}
			_throttled[pid] = background;
			App.Logger.WriteLine(LogIdent, background ? $"Roblox {pid} is in the background, throttled it" : $"Roblox {pid} is focused, running at full speed");
		}
	}

	private void Forget(List<int> games)
	{
		List<int> gone = [];
		foreach (int pid in _throttled.Keys)
		{
			if (!games.Contains(pid))
				gone.Add(pid);
		}
		foreach (int pid in gone)
			_throttled.Remove(pid);
	}

	private void ReleaseAll()
	{
		foreach (KeyValuePair<int, bool> entry in _throttled)
		{
			if (entry.Value)
				Voidstrap.Platform.MacOS.MacOSProcessPolicy.SetBackground(entry.Key, false);
		}
		_throttled.Clear();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_cancellation.Cancel();
		try
		{
			_loop?.Wait(TimeSpan.FromSeconds(2));
		}
		catch (AggregateException)
		{
		}
		_cancellation.Dispose();
		GC.SuppressFinalize(this);
	}
}
