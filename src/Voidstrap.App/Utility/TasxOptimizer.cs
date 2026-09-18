using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Persistable;

namespace Voidstrap.Utility;

internal sealed partial class TasxOptimizer : IDisposable
{
	private const string LogIdent = "TasxOptimizer";

	private const string TargetProcessName = "RobloxPlayerBeta";

	private const int TickIntervalMs = 500;

	private const int ScanEveryTicks = 4;

	private const ProcessPriorityClass FocusedPriority = ProcessPriorityClass.High;

	private const ProcessPriorityClass UnfocusedPriority = ProcessPriorityClass.BelowNormal;

	private const bool MinimizedMemoryTrimming = true;

	private const int ProcessPowerThrottling = 4;

	private const uint PowerThrottlingCurrentVersion = 1;

	private const uint ExecutionSpeed = 0x1;

	private readonly Dictionary<int, Instance> _instances = new Dictionary<int, Instance>();

	private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

	private Task? _loopTask;

	private int _started;

	private bool _disposed;

	private sealed class Instance
	{
		public Instance(Process process)
		{
			Process = process;
		}

		public Process Process { get; }

		public ProcessPriorityClass OriginalPriority { get; set; } = ProcessPriorityClass.Normal;

		public bool? Focused { get; set; }

		public bool WasEverFocused { get; set; }

		public bool TrimmedWhileMinimized { get; set; }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PowerThrottlingState
	{
		public uint Version;

		public uint ControlMask;

		public uint StateMask;
	}

	[LibraryImport("psapi.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EmptyWorkingSet(IntPtr hProcess);

	[LibraryImport("user32.dll")]
	private static partial IntPtr GetForegroundWindow();

	[LibraryImport("user32.dll")]
	private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetProcessInformation(IntPtr hProcess, int informationClass, ref PowerThrottlingState information, uint size);

	public static bool ShouldRun(AppSettings? settings)
	{
		return Platform.IsWindows && settings != null && settings.TasxOptimization;
	}

	public void Start()
	{
		if (_disposed || !Platform.IsWindows || Interlocked.Exchange(ref _started, 1) != 0)
		{
			return;
		}
		App.Logger.WriteLine(LogIdent, "TASX optimization started");
		_loopTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
	}

	private async Task RunAsync(CancellationToken token)
	{
		int tick = 0;
		try
		{
			while (!token.IsCancellationRequested)
			{
				if (tick % ScanEveryTicks == 0)
				{
					Scan();
				}
				ApplyFocusState();
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

	private void Scan()
	{
		try
		{
			foreach (Process process in Process.GetProcessesByName(TargetProcessName))
			{
				if (_instances.ContainsKey(process.Id) || HasExited(process))
				{
					process.Dispose();
					continue;
				}
				Track(process);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Process scan failed: " + ex.Message);
		}

		List<int>? exited = null;
		foreach (KeyValuePair<int, Instance> pair in _instances)
		{
			if (HasExited(pair.Value.Process))
			{
				(exited ??= new List<int>()).Add(pair.Key);
			}
		}
		Forget(exited);
	}

	private void Forget(List<int>? processIds)
	{
		if (processIds == null)
		{
			return;
		}
		foreach (int processId in processIds)
		{
			if (_instances.Remove(processId, out Instance? instance))
			{
				Release(instance);
				App.Logger.WriteLine(LogIdent, "Roblox process " + processId + " exited");
			}
		}
	}

	private void Track(Process process)
	{
		Instance instance = new Instance(process);
		try
		{
			instance.OriginalPriority = process.PriorityClass;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Could not read the original state of process " + process.Id + ": " + ex.Message);
		}
		_instances[process.Id] = instance;
		App.Logger.WriteLine(LogIdent, "Now managing Roblox process " + process.Id);
	}

	private void ApplyFocusState()
	{
		uint foregroundProcessId = GetForegroundProcessId();
		List<int>? exited = null;
		foreach (KeyValuePair<int, Instance> pair in _instances)
		{
			Instance instance = pair.Value;
			if (HasExited(instance.Process))
			{
				(exited ??= new List<int>()).Add(pair.Key);
				continue;
			}
			bool focused = foregroundProcessId != 0 && (uint)pair.Key == foregroundProcessId;
			if (focused)
			{
				instance.WasEverFocused = true;
			}
			if (RobloxProcessOptimizer.IsStillLaunching(instance.Process))
			{
				continue;
			}
			bool alive = true;
			if (instance.Focused != focused)
			{
				instance.Focused = focused;
				alive = Apply(instance, focused);
			}
			if (alive && MinimizedMemoryTrimming && instance.WasEverFocused)
			{
				alive = TrimWhenMinimized(instance, focused);
			}
			if (!alive)
			{
				(exited ??= new List<int>()).Add(pair.Key);
			}
		}
		Forget(exited);
	}

	private static bool Apply(Instance instance, bool focused)
	{
		Process process = instance.Process;
		List<string> errors = new List<string>(3);
		AddError(errors, SetPriority(process, focused ? FocusedPriority : UnfocusedPriority));
		AddError(errors, SetFullSpeed(process, keep: true));
		if (!RobloxProcessOptimizer.SetMemoryPriority(process, low: !focused))
		{
			errors.Add("Memory priority change failed for process " + process.Id);
		}
		if (errors.Count > 0 && IsExiting(process))
		{
			return false;
		}
		foreach (string error in errors)
		{
			App.Logger.WriteLine(LogIdent, error);
		}
		if (errors.Count == 0)
		{
			App.Logger.WriteLine(LogIdent, "Roblox process " + process.Id + (focused ? " is focused, full performance applied" : " lost focus, lower priority applied while keeping full speed"));
		}
		return true;
	}

	private static void AddError(List<string> errors, string? error)
	{
		if (error != null)
		{
			errors.Add(error);
		}
	}

	private static bool TrimWhenMinimized(Instance instance, bool focused)
	{
		if (focused || !RobloxProcessOptimizer.IsMinimized(instance.Process))
		{
			instance.TrimmedWhileMinimized = false;
			return true;
		}
		if (instance.TrimmedWhileMinimized)
		{
			return true;
		}
		instance.TrimmedWhileMinimized = true;
		try
		{
			if (EmptyWorkingSet(instance.Process.Handle))
			{
				instance.Process.Refresh();
				App.Logger.WriteLine(LogIdent, "Trimmed minimized Roblox process " + instance.Process.Id + " to " + instance.Process.WorkingSet64 / 1048576 + " MB");
				return true;
			}
			int code = Marshal.GetLastWin32Error();
			if (IsExiting(instance.Process))
			{
				return false;
			}
			App.Logger.WriteLine(LogIdent, "Memory trim failed for process " + instance.Process.Id + " with code " + code);
		}
		catch (Exception ex)
		{
			if (IsExiting(instance.Process))
			{
				return false;
			}
			App.Logger.WriteLine(LogIdent, "Memory trim failed for process " + instance.Process.Id + ": " + ex.Message);
		}
		return true;
	}

	private static string? SetPriority(Process process, ProcessPriorityClass priority)
	{
		try
		{
			if (process.PriorityClass != priority)
			{
				process.PriorityClass = priority;
			}
			return null;
		}
		catch (Exception ex)
		{
			return "Priority change failed for process " + process.Id + ": " + ex.Message;
		}
	}

	private static string? SetFullSpeed(Process process, bool keep)
	{
		PowerThrottlingState state = new PowerThrottlingState
		{
			Version = PowerThrottlingCurrentVersion,
			ControlMask = keep ? ExecutionSpeed : 0u,
			StateMask = 0u
		};
		try
		{
			if (!SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
			{
				return "Power throttling change failed for process " + process.Id + " with code " + Marshal.GetLastWin32Error();
			}
			return null;
		}
		catch (Exception ex)
		{
			return "Power throttling change failed for process " + process.Id + ": " + ex.Message;
		}
	}

	private static uint GetForegroundProcessId()
	{
		try
		{
			IntPtr window = GetForegroundWindow();
			if (window == IntPtr.Zero)
			{
				return 0u;
			}
			GetWindowThreadProcessId(window, out uint processId);
			return processId;
		}
		catch
		{
			return 0u;
		}
	}

	private static bool HasExited(Process process)
	{
		try
		{
			return process.HasExited;
		}
		catch
		{
			return true;
		}
	}

	private static bool IsExiting(Process process)
	{
		try
		{
			return process.WaitForExit(250);
		}
		catch
		{
			return true;
		}
	}

	private static void Release(Instance instance)
	{
		Process process = instance.Process;
		if (!HasExited(process))
		{
			List<string> errors = new List<string>(3);
			AddError(errors, SetFullSpeed(process, keep: false));
			AddError(errors, SetPriority(process, instance.OriginalPriority));
			RobloxProcessOptimizer.SetMemoryPriority(process, low: false);
			if (errors.Count > 0 && !IsExiting(process))
			{
				foreach (string error in errors)
				{
					App.Logger.WriteLine(LogIdent, error);
				}
			}
		}
		process.Dispose();
	}

	private void ReleaseAll()
	{
		foreach (Instance instance in _instances.Values)
		{
			Release(instance);
		}
		_instances.Clear();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_cancellationTokenSource.Cancel();
		try
		{
			_loopTask?.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Shutdown failed: " + ex.Message);
		}
		_loopTask = null;
		_cancellationTokenSource.Dispose();
		App.Logger.WriteLine(LogIdent, "TASX optimization stopped");
		GC.SuppressFinalize(this);
	}
}
