using System;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public sealed class AsyncMutex : IAsyncDisposable
{
	private readonly bool _initiallyOwned;

	private readonly string _name;

	private Task? _mutexTask;

	private ManualResetEventSlim? _releaseEvent;

	private CancellationTokenSource? _cts;

	public AsyncMutex(bool initiallyOwned, string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Mutex name cannot be null or whitespace.", "name");
		}
		_initiallyOwned = initiallyOwned;
		_name = name;
	}

	public Task AcquireAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		TaskCompletionSource tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_releaseEvent = new ManualResetEventSlim(initialState: false);
		_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_mutexTask = Task.Factory.StartNew(delegate
		{
			try
			{
				using Mutex mutex = new Mutex(_initiallyOwned, _name);
				CancellationToken token = _cts.Token;
				try
				{
					if (WaitHandle.WaitAny(new WaitHandle[2] { mutex, token.WaitHandle }) != 0)
					{
						tcs.TrySetCanceled(token);
						return;
					}
				}
				catch (AbandonedMutexException)
				{
				}
				tcs.TrySetResult();
				_releaseEvent.Wait(token);
				mutex.ReleaseMutex();
			}
			catch (OperationCanceledException)
			{
				tcs.TrySetCanceled(_cts.Token);
			}
			catch (Exception exception)
			{
				tcs.TrySetException(exception);
			}
		}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		return tcs.Task;
	}

	public async Task ReleaseAsync()
	{
		_releaseEvent?.Set();
		if (_mutexTask != null)
		{
			await _mutexTask.ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			_cts?.Cancel();
			await ReleaseAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_releaseEvent?.Dispose();
			_cts?.Dispose();
		}
	}
}
