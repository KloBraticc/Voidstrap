using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Shell;

namespace Voidstrap.UI.Utility;

internal static class TaskbarProgress
{
	private enum TaskbarStates
	{
		NoProgress = 0,
		Indeterminate = 1,
		Normal = 2,
		Error = 4,
		Paused = 8
	}

	[ComImport]
	[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ITaskbarList3
	{
		void HrInit();

		void AddTab(nint hwnd);

		void DeleteTab(nint hwnd);

		void ActivateTab(nint hwnd);

		void SetActiveAlt(nint hwnd);

		void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

		void SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);

		void SetProgressState(nint hwnd, TaskbarStates state);
	}

	[ComImport]
	[Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
	[ClassInterface(ClassInterfaceType.None)]
	private class TaskbarInstance
	{
	}

	private static readonly Lock _lock = new();

	private static ITaskbarList3? _taskbar;

	private static ITaskbarList3 GetTaskbar()
	{
		lock (_lock)
		{
			if (_taskbar == null)
			{
				_taskbar = (ITaskbarList3)new TaskbarInstance();
				_taskbar.HrInit();
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
		GetTaskbar().SetProgressState(windowHandle, ConvertEnum(state));
	}

	public static void SetProgressValue(nint windowHandle, int value, int maximum)
	{
		GetTaskbar().SetProgressValue(windowHandle, (ulong)value, (ulong)maximum);
	}

	public static void Dispose()
	{
		lock (_lock)
		{
			if (_taskbar != null)
			{
				Marshal.ReleaseComObject(_taskbar);
				_taskbar = null;
			}
		}
	}
}
