using System;
using System.Threading;

namespace Voidstrap.Utility;

public class InterProcessLock : IDisposable
{
	public Mutex Mutex { get; private set; }

	public bool IsAcquired { get; private set; }

	private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

	public InterProcessLock(string name)
		: this(name, TimeSpan.Zero)
	{
	}

	public InterProcessLock(string name, TimeSpan timeout)
	{
		Mutex = new Mutex(initiallyOwned: false, "Voidstrap-" + name);
		try
		{
			IsAcquired = Mutex.WaitOne(timeout);
		}
		catch (AbandonedMutexException)
		{
			IsAcquired = true;
		}
	}

	public void Dispose()
	{
		if (IsAcquired)
		{
			if (Environment.CurrentManagedThreadId == _ownerThreadId)
			{
				try
				{
					Mutex.ReleaseMutex();
				}
				catch (ApplicationException)
				{
				}
			}
			IsAcquired = false;
		}
		try
		{
			Mutex.Dispose();
		}
		catch
		{
		}
		GC.SuppressFinalize(this);
	}
}
