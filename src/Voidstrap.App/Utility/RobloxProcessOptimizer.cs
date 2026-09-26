using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Persistable;

namespace Voidstrap.Utility;

internal sealed partial class RobloxProcessOptimizer : IDisposable
{
	private const int PollIntervalMs = 2000;

	private const int ProcessMemoryPriorityClass = 0;

	private const uint MemoryPriorityLow = 2;

	private const uint MemoryPriorityNormal = 5;

	private const int LaunchGraceMs = 25000;

	private const int TransitionGraceMs = 30000;

	private static long _lastTransitionTicks = long.MinValue / 2;

	public static void NoteGameTransition()
	{
		Interlocked.Exchange(ref _lastTransitionTicks, Environment.TickCount64);
	}

	private static readonly int ProcessorCount = Environment.ProcessorCount;

	private readonly int _processId;

	private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

	private Task? _loopTask;

	private bool _disposed;

	private bool _memoryPriorityLowered;

	private bool _trimmedWhileMinimized;

	private ProcessPriorityClass? _lastPriority;

	private int? _lastCpuLimit;

	private int? _lastMemoryLimitMb = -1;

	private IntPtr? _originalAffinity;

	private bool? _originalPriorityBoostEnabled;

	private int _started;

	[LibraryImport("psapi.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EmptyWorkingSet(IntPtr hProcess);

	[LibraryImport("user32.dll")]
	private static partial IntPtr GetForegroundWindow();

	[LibraryImport("user32.dll")]
	private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsIconic(IntPtr hWnd);

	[LibraryImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetProcessMemoryPriority(IntPtr hProcess, int informationClass, ref uint memoryPriority, uint size);

	public RobloxProcessOptimizer(int processId)
	{
		_processId = processId;
	}

	public static bool IsStillLaunching(Process? process)
	{
		if (process == null)
		{
			return false;
		}
		try
		{
			if (process.HasExited)
			{
				return false;
			}
			if (Environment.TickCount64 - Interlocked.Read(ref _lastTransitionTicks) < TransitionGraceMs)
			{
				return true;
			}
			if ((DateTime.Now - process.StartTime).TotalMilliseconds < LaunchGraceMs)
			{
				return true;
			}
			process.Refresh();
			return process.MainWindowHandle == IntPtr.Zero;
		}
		catch (Exception)
		{
			return false;
		}
	}

	internal static bool SetMemoryPriority(Process process, bool low)
	{
		if (!Platform.IsWindows)
		{
			return true;
		}
		uint priority = low ? MemoryPriorityLow : MemoryPriorityNormal;
		try
		{
			return SetProcessMemoryPriority(process.Handle, ProcessMemoryPriorityClass, ref priority, sizeof(uint));
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsMinimized(Process process)
	{
		if (!Platform.IsWindows)
		{
			return false;
		}
		try
		{
			process.Refresh();
			IntPtr window = process.MainWindowHandle;
			return window != IntPtr.Zero && IsIconic(window);
		}
		catch
		{
			return false;
		}
	}

	public static bool ShouldRun(AppSettings? settings)
	{
		if (settings == null)
		{
			return false;
		}
		return settings.OptimizeRoblox
			|| settings.TasxOptimization
			|| settings.BypassEmulationOverhead
			|| settings.RobloxEfficiencyMode
			|| settings.RobloxMemoryLimitEnabled
			|| settings.MultiAccount
			|| settings.ReduceMemoryOutOfFocus
			|| !IsAutomaticCpuLimit(settings.SelectedCpuPriority)
			|| !IsNormalPriority(settings.PriorityLimit);
	}

	public static void ApplyLaunchProfile(Process process)
	{
		if (process == null || process.HasExited || !IsRobloxPlayer(process))
		{
			return;
		}
		AppSettings settings = App.Settings.Prop;
		if (settings.RobloxMemoryLimitEnabled)
		{
			RobloxMemoryLimit.Apply(process, settings.RobloxMemoryLimitMb);
		}
		if (settings.BypassEmulationOverhead)
		{
			EmulationBypassService.ApplyProcessBypass(process);
		}
		if (!ShouldRun(settings) || settings.TasxOptimization)
		{
			return;
		}
		TryApplyPriority(process, ResolvePriority(settings));
	}

	public void Start()
	{
		if (_disposed || Interlocked.Exchange(ref _started, 1) != 0)
		{
			return;
		}
		_loopTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
	}

	private async Task RunAsync(CancellationToken token)
	{
		try
		{
			using Process? process = TryGetProcess();
			if (process == null || process.HasExited || !IsRobloxPlayer(process))
				return;
			while (!token.IsCancellationRequested && !process.HasExited)
			{
				ApplySettings(process);
				await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxProcessOptimizer", "Runtime optimizer failed: " + ex.Message);
		}
		finally
		{
			RestoreProcessState();
		}
	}

	private void ApplySettings(Process process)
	{
		AppSettings settings = App.Settings.Prop;
		int? memoryLimit = settings.RobloxMemoryLimitEnabled ? RobloxMemoryLimit.Clamp(settings.RobloxMemoryLimitMb) : null;
		if (memoryLimit != _lastMemoryLimitMb && RobloxMemoryLimit.Apply(process, memoryLimit))
		{
			_lastMemoryLimitMb = memoryLimit;
		}
		if (settings.TasxOptimization)
		{
			return;
		}
		bool focused = IsProcessFocused();
		ProcessPriorityClass desiredPriority = !focused && settings.RobloxEfficiencyMode ? ProcessPriorityClass.Idle : (!focused && settings.ReduceMemoryOutOfFocus ? ProcessPriorityClass.BelowNormal : ResolvePriority(settings));
		if (_lastPriority != desiredPriority)
		{
			TryApplyPriority(process, desiredPriority);
			_lastPriority = desiredPriority;
		}
		if (settings.OptimizeRoblox && !_originalPriorityBoostEnabled.HasValue)
		{
			try
			{
				_originalPriorityBoostEnabled = process.PriorityBoostEnabled;
			}
			catch
			{
			}
		}
		bool enablePriorityBoost = settings.OptimizeRoblox && (focused || !settings.ReduceMemoryOutOfFocus);
		if (enablePriorityBoost)
		{
			try
			{
				if (!process.PriorityBoostEnabled)
				{
					process.PriorityBoostEnabled = true;
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("RobloxProcessOptimizer", "Priority boost could not be enabled: " + ex.Message);
			}
		}
		else if (_originalPriorityBoostEnabled.HasValue)
		{
			try
			{
				if (process.PriorityBoostEnabled != _originalPriorityBoostEnabled.Value)
				{
					process.PriorityBoostEnabled = _originalPriorityBoostEnabled.Value;
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("RobloxProcessOptimizer", "Priority boost could not be restored: " + ex.Message);
			}
		}
		int? cpuLimit = GetCpuLimit(settings.SelectedCpuPriority);
		if (_lastCpuLimit != cpuLimit)
		{
			if (cpuLimit.HasValue && !_originalAffinity.HasValue)
			{
				try
				{
					_originalAffinity = process.ProcessorAffinity;
				}
				catch
				{
				}
			}
			TryApplyCpuLimit(process, settings.SelectedCpuPriority, _originalAffinity);
			_lastCpuLimit = cpuLimit;
		}
		bool lowerMemoryPriority = settings.ReduceMemoryOutOfFocus && !focused;
		if (_memoryPriorityLowered != lowerMemoryPriority && SetMemoryPriority(process, lowerMemoryPriority))
		{
			_memoryPriorityLowered = lowerMemoryPriority;
		}
		bool minimized = settings.ReduceMemoryOutOfFocus && !focused && !IsStillLaunching(process) && IsMinimized(process);
		if (!minimized)
		{
			_trimmedWhileMinimized = false;
			return;
		}
		if (!_trimmedWhileMinimized)
		{
			_trimmedWhileMinimized = true;
			TrimWorkingSet(process);
		}
	}

	private static void TrimWorkingSet(Process process)
	{
		if (!Platform.IsWindows)
		{
			return;
		}
		try
		{
			if (EmptyWorkingSet(process.Handle))
			{
				process.Refresh();
				App.Logger.WriteLine("RobloxProcessOptimizer", "Trimmed minimized Roblox working set to " + process.WorkingSet64 / 1048576 + " MB");
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxProcessOptimizer", "Working set trim failed: " + ex.Message);
		}
	}

	private void RestoreProcessState()
	{
		RobloxMemoryLimit.Forget(_processId);
		using Process? process = TryGetProcess();
		if (process == null || process.HasExited)
		{
			return;
		}
		if (_originalAffinity.HasValue)
		{
			try
			{
				process.ProcessorAffinity = _originalAffinity.Value;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("RobloxProcessOptimizer", "CPU affinity could not be restored: " + ex.Message);
			}
		}
		if (_originalPriorityBoostEnabled.HasValue)
		{
			try
			{
				process.PriorityBoostEnabled = _originalPriorityBoostEnabled.Value;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("RobloxProcessOptimizer", "Priority boost could not be restored: " + ex.Message);
			}
		}
		if (_memoryPriorityLowered)
		{
			SetMemoryPriority(process, low: false);
		}
		if (!App.Settings.Prop.RobloxMemoryLimitEnabled)
		{
			RobloxMemoryLimit.Apply(process, null);
		}
		if (!ShouldRun(App.Settings.Prop))
		{
			TryApplyPriority(process, ProcessPriorityClass.Normal);
		}
	}

	private Process? TryGetProcess()
	{
		try
		{
			return Process.GetProcessById(_processId);
		}
		catch
		{
			return null;
		}
	}

	private bool IsProcessFocused()
	{
		if (!Platform.IsWindows)
		{
			return IsLinuxRuntimeFocused();
		}

		try
		{
			IntPtr foregroundWindow = GetForegroundWindow();
			if (foregroundWindow == IntPtr.Zero)
			{
				return false;
			}
			GetWindowThreadProcessId(foregroundWindow, out uint processId);
			return processId == (uint)_processId;
		}
		catch
		{
			return false;
		}
	}

	private static void TryApplyPriority(Process process, ProcessPriorityClass priority)
	{
		try
		{
			if (process.PriorityClass != priority)
			{
				process.PriorityClass = priority;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxProcessOptimizer", "Priority change failed: " + ex.Message);
		}
	}

	private static void TryApplyCpuLimit(Process process, string? selection, IntPtr? originalAffinity)
	{
		int? limit = GetCpuLimit(selection);
		if (!limit.HasValue)
		{
			if (originalAffinity.HasValue)
			{
				try
				{
					process.ProcessorAffinity = originalAffinity.Value;
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("RobloxProcessOptimizer", "CPU affinity could not be restored: " + ex.Message);
				}
			}
			return;
		}
		int processorCount = ProcessorCount;
		int pointerBits = IntPtr.Size * 8;
		if (processorCount > pointerBits)
		{
			App.Logger.WriteLine("RobloxProcessOptimizer", "CPU limit skipped because this system uses Windows processor groups");
			return;
		}
		if (limit.Value >= processorCount)
		{
			if (originalAffinity.HasValue)
			{
				try
				{
					process.ProcessorAffinity = originalAffinity.Value;
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("RobloxProcessOptimizer", "CPU affinity could not be restored: " + ex.Message);
				}
			}
			return;
		}
		try
		{
			ulong mask = (1UL << limit.Value) - 1UL;
			process.ProcessorAffinity = new IntPtr(unchecked((long)mask));
			App.Logger.WriteLine("RobloxProcessOptimizer", "Roblox CPU limit set to " + limit.Value + " logical processors");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxProcessOptimizer", "CPU limit change failed: " + ex.Message);
		}
	}

	internal static ProcessPriorityClass ResolvePriority(AppSettings settings)
	{
		string priority = settings.PriorityLimit?.Trim() ?? "Normal";
		if (priority.Equals("Realtime", StringComparison.OrdinalIgnoreCase))
		{
			return ProcessPriorityClass.High;
		}
		if (priority.Equals("High", StringComparison.OrdinalIgnoreCase))
		{
			return ProcessPriorityClass.High;
		}
		if (priority.Equals("Above Normal", StringComparison.OrdinalIgnoreCase) || priority.Equals("AboveNormal", StringComparison.OrdinalIgnoreCase))
		{
			return ProcessPriorityClass.AboveNormal;
		}
		if (priority.Equals("Below Normal", StringComparison.OrdinalIgnoreCase) || priority.Equals("BelowNormal", StringComparison.OrdinalIgnoreCase))
		{
			return ProcessPriorityClass.BelowNormal;
		}
		if (priority.Equals("Low", StringComparison.OrdinalIgnoreCase) || priority.Equals("Idle", StringComparison.OrdinalIgnoreCase))
		{
			return ProcessPriorityClass.Idle;
		}
		return settings.OptimizeRoblox ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
	}

	internal static int? GetCpuLimit(string? selection)
	{
		if (IsAutomaticCpuLimit(selection))
		{
			return null;
		}
		string value = selection!.Trim().Split(' ')[0];
		if (!int.TryParse(value, out int limit) || limit < 1)
		{
			return null;
		}
		return Math.Min(limit, ProcessorCount);
	}

	private static bool IsAutomaticCpuLimit(string? selection)
	{
		return string.IsNullOrWhiteSpace(selection) || selection.Equals("Automatic", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsNormalPriority(string? priority)
	{
		return string.IsNullOrWhiteSpace(priority) || priority.Equals("Normal", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsLinuxRuntimeFocused()
	{
		try
		{
			return Voidstrap.Platform.Linux.LinuxWindowInterop.FindRuntimeWindow().Focused;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool IsRobloxPlayer(Process process)
	{
		try
		{
			return process.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase)
				|| process.ProcessName.Equals("Roblox", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
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
			App.Logger.WriteLine("RobloxProcessOptimizer", "Shutdown failed: " + ex.Message);
		}
		_loopTask = null;
		_cancellationTokenSource.Dispose();
		GC.SuppressFinalize(this);
	}
}
