using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Persistable;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal sealed class LinuxRobloxResourceOptimizer : IDisposable
{
	private const string LogIdent = "LinuxResourceOptimizer";

	private const int PollIntervalMs = 2000;

	private const int TrimAfterUnfocusedMs = 60000;

	private const int DefaultWeight = 100;

	private const string NoMemoryLimit = "infinity";

	private static readonly int ProcessorCount = Environment.ProcessorCount;

	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

	private Task? _loopTask;

	private int _started;

	private bool _disposed;

	private LinuxSoberScope? _scope;

	private int? _appliedWeight;

	private string? _appliedMemoryHigh;

	private int? _appliedCpuLimit;

	private byte[]? _originalAffinity;

	private long _unfocusedSince;

	private bool _trimmed;

	private string? _lastFailure;

	public static bool ShouldRun(AppSettings? settings)
	{
		return Platform.IsLinux
			&& settings != null
			&& LinuxSoberResources.IsSupported
			&& (settings.TasxOptimization || RobloxProcessOptimizer.ShouldRun(settings));
	}

	public void Start()
	{
		if (_disposed || Interlocked.Exchange(ref _started, 1) != 0)
		{
			return;
		}
		_loopTask = Task.Run(() => RunAsync(_cancellation.Token));
	}

	private async Task RunAsync(CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				Apply();
				await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The Linux resource optimizer stopped: " + ex.Message);
		}
		finally
		{
			Restore();
		}
	}

	private void Apply()
	{
		if (!LinuxSoberResources.TryGetScope(out LinuxSoberScope scope))
		{
			_scope = null;
			ResetApplied();
			return;
		}
		if (_scope != scope)
		{
			_scope = scope;
			ResetApplied();
		}

		AppSettings settings = App.Settings.Prop;
		bool focused = IsFocused();
		int weight = ResolveWeight(settings, focused);
		string memoryHigh = settings.RobloxMemoryLimitEnabled
			? RobloxMemoryLimit.Clamp(settings.RobloxMemoryLimitMb) + "M"
			: NoMemoryLimit;
		List<string> assignments = new List<string>();
		if (_appliedWeight != weight)
		{
			assignments.Add("CPUWeight=" + weight);
		}
		if (_appliedMemoryHigh != memoryHigh)
		{
			assignments.Add("MemoryHigh=" + memoryHigh);
		}
		if (assignments.Count > 0)
		{
			if (LinuxSoberResources.TrySetProperties(scope, assignments, out string error))
			{
				if (_appliedWeight != weight)
				{
					App.Logger.WriteLine(LogIdent, "Sober CPU share set to " + weight + (focused ? " while Roblox is focused" : " while Roblox is unfocused"));
				}
				if (_appliedMemoryHigh != memoryHigh)
				{
					App.Logger.WriteLine(LogIdent, memoryHigh == NoMemoryLimit ? "Sober memory limit removed" : "Sober memory limit set to " + memoryHigh + "B");
				}
				_appliedWeight = weight;
				_appliedMemoryHigh = memoryHigh;
				_lastFailure = null;
			}
			else if (_lastFailure != error)
			{
				_lastFailure = error;
				App.Logger.WriteLine(LogIdent, "Sober resource controls could not be applied: " + error);
			}
		}
		ApplyCpuLimit(settings);
		ApplyTrim(settings, focused, scope);
	}

	private void ApplyCpuLimit(AppSettings settings)
	{
		int? limit = RobloxProcessOptimizer.GetCpuLimit(settings.SelectedCpuPriority);
		if (limit.HasValue && limit.Value >= ProcessorCount)
		{
			limit = null;
		}
		IReadOnlyList<int> processIds = LinuxSoberProcessProbe.GetSandboxProcessIds();
		if (limit.HasValue)
		{
			if (_originalAffinity == null && processIds.Count > 0)
			{
				_originalAffinity = LinuxSoberResources.TryReadAffinity(processIds[0]);
			}
			int threads = LinuxSoberResources.ApplyAffinity(processIds, LinuxSoberResources.CreateAffinity(limit.Value));
			if (_appliedCpuLimit != limit)
			{
				_appliedCpuLimit = limit;
				App.Logger.WriteLine(LogIdent, "Roblox CPU limit set to " + limit.Value + " logical processors across " + threads + " Sober threads");
			}
			return;
		}
		if (_appliedCpuLimit.HasValue)
		{
			RestoreAffinity(processIds);
			_appliedCpuLimit = null;
			App.Logger.WriteLine(LogIdent, "Roblox CPU limit removed");
		}
	}

	private void ApplyTrim(AppSettings settings, bool focused, LinuxSoberScope scope)
	{
		if (!settings.MultiAccount || focused)
		{
			_unfocusedSince = 0;
			_trimmed = false;
			return;
		}
		long now = Environment.TickCount64;
		if (_unfocusedSince == 0)
		{
			_unfocusedSince = now;
			return;
		}
		if (_trimmed || now - _unfocusedSince < TrimAfterUnfocusedMs)
		{
			return;
		}
		_trimmed = true;
		long before = LinuxSoberResources.ReadMemoryCurrent(scope);
		LinuxSoberResources.TryReclaimMemory(scope, before / 4);
		long after = LinuxSoberResources.ReadMemoryCurrent(scope);
		App.Logger.WriteLine(LogIdent, "Released " + Math.Max(0, before - after) / 1048576 + " MB from Sober after it stayed unfocused");
	}

	private void RestoreAffinity(IReadOnlyList<int> processIds)
	{
		LinuxSoberResources.ApplyAffinity(processIds, _originalAffinity ?? LinuxSoberResources.CreateAffinity(int.MaxValue));
		_originalAffinity = null;
	}

	private void Restore()
	{
		if (!LinuxSoberResources.TryGetScope(out LinuxSoberScope scope) || _scope != scope)
		{
			return;
		}
		List<string> assignments = new List<string>();
		if (_appliedWeight.HasValue && _appliedWeight != DefaultWeight)
		{
			assignments.Add("CPUWeight=" + DefaultWeight);
		}
		if (_appliedMemoryHigh != null && _appliedMemoryHigh != NoMemoryLimit)
		{
			assignments.Add("MemoryHigh=" + NoMemoryLimit);
		}
		if (assignments.Count > 0 && !LinuxSoberResources.TrySetProperties(scope, assignments, out string error))
		{
			App.Logger.WriteLine(LogIdent, "Sober resource controls could not be restored: " + error);
		}
		if (_appliedCpuLimit.HasValue)
		{
			RestoreAffinity(LinuxSoberProcessProbe.GetSandboxProcessIds());
		}
		ResetApplied();
	}

	private void ResetApplied()
	{
		_appliedWeight = null;
		_appliedMemoryHigh = null;
		_appliedCpuLimit = null;
		_originalAffinity = null;
		_unfocusedSince = 0;
		_trimmed = false;
	}

	private static bool IsFocused()
	{
		LinuxWindowGeometry geometry = LinuxWindowInterop.FindRuntimeWindow();
		return !geometry.Valid || geometry.Focused;
	}

	private static int ResolveWeight(AppSettings settings, bool focused)
	{
		if (settings.TasxOptimization)
		{
			return focused ? 400 : 50;
		}
		if (!focused && settings.RobloxEfficiencyMode)
		{
			return 10;
		}
		if (!focused && settings.ReduceMemoryOutOfFocus)
		{
			return 50;
		}
		string choice = settings.PriorityLimit?.Trim() ?? "";
		if (choice.Length == 0 || choice.Equals("Normal", StringComparison.OrdinalIgnoreCase))
		{
			return settings.OptimizeRoblox && focused ? 200 : DefaultWeight;
		}
		return RobloxProcessOptimizer.ResolvePriority(settings) switch
		{
			ProcessPriorityClass.Idle => 10,
			ProcessPriorityClass.BelowNormal => 50,
			ProcessPriorityClass.AboveNormal => 200,
			ProcessPriorityClass.High => 400,
			ProcessPriorityClass.RealTime => 400,
			_ => DefaultWeight
		};
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_cancellation.Cancel();
		try
		{
			_loopTask?.Wait(3000);
		}
		catch (AggregateException)
		{
		}
		_cancellation.Dispose();
		GC.SuppressFinalize(this);
	}
}
