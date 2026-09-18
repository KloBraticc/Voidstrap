using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Shell;

namespace Voidstrap.UI.Utility;

internal static unsafe partial class TaskbarProgress
{
	private enum TaskbarStates
	{
		NoProgress = 0,
		Indeterminate = 1,
		Normal = 2,
		Error = 4,
		Paused = 8
	}

	private static readonly Guid TaskbarClassId = new Guid("56fdf344-fd6d-11d0-958a-006097c9a090");

	private static readonly Guid TaskbarInterfaceId = new Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

	[LibraryImport("ole32.dll")]
	private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

	private static readonly Lock _lock = new();

	private static nint _taskbar;

	private static nint GetTaskbar()
	{
		lock (_lock)
		{
			if (_taskbar == 0)
			{
				Marshal.ThrowExceptionForHR(CoCreateInstance(in TaskbarClassId, 0, 1, in TaskbarInterfaceId, out nint instance));
				int hr = ((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)instance)[3])(instance);
				if (hr < 0)
				{
					Marshal.Release(instance);
					Marshal.ThrowExceptionForHR(hr);
				}
				_taskbar = instance;
			}
			return _taskbar;
		}
	}

	private static TaskbarStates ConvertEnum(TaskbarItemProgressState state)
	{
		return state switch
		{
			TaskbarItemProgressState.None => TaskbarStates.NoProgress, 
			TaskbarItemProgressState.Indeterminate => TaskbarStates.Indeterminate, 
			TaskbarItemProgressState.Normal => TaskbarStates.Normal, 
			TaskbarItemProgressState.Error => TaskbarStates.Error, 
			TaskbarItemProgressState.Paused => TaskbarStates.Paused, 
			_ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown TaskbarItemProgressState"), 
		};
	}

	public static void SetProgressState(nint windowHandle, TaskbarItemProgressState state)
	{
		nint taskbar = GetTaskbar();
		Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int, int>)(*(nint**)taskbar)[10])(taskbar, windowHandle, (int)ConvertEnum(state)));
	}

	public static void SetProgressValue(nint windowHandle, int value, int maximum)
	{
		nint taskbar = GetTaskbar();
		Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, ulong, ulong, int>)(*(nint**)taskbar)[9])(taskbar, windowHandle, (ulong)value, (ulong)maximum));
	}

	public static void Dispose()
	{
		lock (_lock)
		{
			if (_taskbar != 0)
			{
				Marshal.Release(_taskbar);
				_taskbar = 0;
			}
		}
	}
}