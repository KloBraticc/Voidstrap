using System;
using System.Collections.Generic;
using System.Threading;

namespace Voidstrap.Utility;

public static class MultiInstanceLock
{
	private const string LogIdent = "MultiInstanceLock";

	private const string SingletonMutexName = "ROBLOX_singletonMutex";

	private const string SingletonEventName = "ROBLOX_singletonEvent";

	private static readonly object Gate = new object();

	private static Mutex? _singleton;

	private static readonly List<WaitHandle> _singletonHandles = [];

	public static bool IsHeld
	{
		get
		{
			lock (Gate)
			{
				return _singleton != null;
			}
		}
	}

	public const string EnvironmentFlag = "VOIDSTRAP_MULTI_INSTANCE";

	public static bool Enabled
	{
		get
		{
			if (!Platform.IsWindows)
			{
				return false;
			}
			if (LaunchedForMultiInstance)
			{
				return true;
			}
			try
			{
				return App.Settings.Prop.MultiInstanceLaunching;
			}
			catch
			{
				return false;
			}
		}
	}

	private static bool LaunchedForMultiInstance => string.Equals(Environment.GetEnvironmentVariable(EnvironmentFlag), "1", StringComparison.Ordinal);

	public static bool Acquire()
	{
		if (!Platform.IsWindows)
		{
			return false;
		}
		lock (Gate)
		{
			if (_singleton != null)
			{
				return true;
			}
			try
			{
				Mutex.OpenExisting(SingletonMutexName).Close();
			}
			catch
			{
			}
			try
			{
				Mutex created = new Mutex(true, SingletonMutexName, out bool owned);
				if (!owned)
				{
					created.Dispose();
					App.Logger?.WriteLine(LogIdent, "Another program already holds the Roblox singleton lock, multi instance may not work");
					return false;
				}
				_singleton = created;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The Roblox singleton lock could not be taken: " + ex.Message);
				return false;
			}
			AcquireSingletonEvents();
			App.Logger?.WriteLine(LogIdent, "Multi instance launching is armed");
			return true;
		}
	}

	private static void AcquireSingletonEvents()
	{
		foreach (string name in new string[] { SingletonEventName, "Global\\" + SingletonEventName, "Global\\" + SingletonMutexName })
		{
			try
			{
				EventWaitHandle handle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out bool created);
				_singletonHandles.Add(handle);
				App.Logger?.WriteLine(LogIdent, created ? "Holding " + name : "Joined the existing " + name);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, name + " could not be held: " + ex.Message);
			}
		}
	}

	private static void ReleaseSingletonEvents()
	{
		foreach (WaitHandle handle in _singletonHandles)
		{
			try
			{
				handle.Dispose();
			}
			catch
			{
			}
		}
		_singletonHandles.Clear();
	}

	public static void Release()
	{
		lock (Gate)
		{
			ReleaseSingletonEvents();
			if (_singleton == null)
			{
				return;
			}
			try
			{
				_singleton.ReleaseMutex();
			}
			catch
			{
			}
			try
			{
				_singleton.Dispose();
			}
			catch
			{
			}
			_singleton = null;
			App.Logger?.WriteLine(LogIdent, "Multi instance launching is disarmed");
		}
	}
}
