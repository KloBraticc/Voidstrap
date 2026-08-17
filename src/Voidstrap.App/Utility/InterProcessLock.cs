using System;
using System.Threading;

namespace Voidstrap.Utility;

public class InterProcessLock : IDisposable
{
	public Mutex Mutex { get; private set; }

	public bool IsAcquired { get; private set; }

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
			try
			{
				Mutex.ReleaseMutex();
			}
			catch
			{
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
