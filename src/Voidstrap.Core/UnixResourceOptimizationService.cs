using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class UnixResourceOptimizationService : IResourceOptimizationService
{
	private static readonly int ProcessorCount = Environment.ProcessorCount;

	private readonly IProcessService _processes;
	private readonly bool _supportsCpuAffinity;

	public UnixResourceOptimizationService(IProcessService processes, CapabilityDescriptor capability, bool supportsCpuAffinity)
	{
		_processes = processes;
		Capability = capability;
		_supportsCpuAffinity = supportsCpuAffinity;
	}

	public CapabilityDescriptor Capability { get; }

	public async Task<OperationResult<ResourceOptimizationResult>> ApplyAsync(ResourceOptimizationRequest request, CancellationToken cancellationToken = default)
	{
		OperationResult validation = Validate(request);
		if (!validation.Succeeded)
		{
			return CopyFailure<ResourceOptimizationResult>(validation.Failure);
		}

		if (request.Priority is ResourcePriority.AboveNormal or ResourcePriority.High)
		{
			return OperationResult<ResourceOptimizationResult>.Fail(
				"PriorityElevationRequiresPermission",
				"Raising process priority requires elevated system permission",
				CapabilityState.RequiresPermission);
		}

		try
		{
			using Process process = Process.GetProcessById(request.ProcessId);
			if (process.HasExited)
			{
				return OperationResult<ResourceOptimizationResult>.Fail("ProcessExited", "The Roblox process has already exited");
			}
		}
		catch (ArgumentException)
		{
			return OperationResult<ResourceOptimizationResult>.Fail("ProcessMissing", "The Roblox process could not be found");
		}
		catch (InvalidOperationException)
		{
			return OperationResult<ResourceOptimizationResult>.Fail("ProcessUnavailable", "The Roblox process could not be inspected");
		}

		bool cpuApplied = false;
		if (request.CpuLimit is int cpuLimit && cpuLimit < ProcessorCount)
		{
			OperationResult tasksetResult = await ApplyCpuAffinityAsync(request.ProcessId, cpuLimit, cancellationToken);
			if (!tasksetResult.Succeeded)
			{
				return CopyFailure<ResourceOptimizationResult>(tasksetResult.Failure);
			}

			cpuApplied = true;
		}

		bool priorityApplied = false;
		int? niceValue = GetNiceValue(request.Priority);
		if (niceValue.HasValue)
		{
			OperationResult reniceResult = await ApplyNiceValueAsync(request.ProcessId, niceValue.Value, cancellationToken);
			if (!reniceResult.Succeeded)
			{
				return CopyFailure<ResourceOptimizationResult>(reniceResult.Failure);
			}

			priorityApplied = true;
		}

		string summary = priorityApplied || cpuApplied
			? "The launch resource profile was applied."
			: "No launch resource profile is selected.";
		return OperationResult<ResourceOptimizationResult>.Success(new ResourceOptimizationResult(request.ProcessId, priorityApplied, cpuApplied, summary));
	}

	private OperationResult Validate(ResourceOptimizationRequest request)
	{
		if (request.ProcessId < 1)
		{
			return OperationResult.Fail("ProcessIdInvalid", "A valid Roblox process is required");
		}

		if (request.CpuLimit is int cpuLimit && (cpuLimit < 1 || cpuLimit > ProcessorCount))
		{
			return OperationResult.Fail("CpuLimitInvalid", "The CPU limit is outside the available processor range");
		}

		if (request.CpuLimit.HasValue && !_supportsCpuAffinity)
		{
			return OperationResult.Fail("CpuLimitUnsupported", "CPU affinity is not available on this platform");
		}

		return OperationResult.Success();
	}

	private async Task<OperationResult> ApplyCpuAffinityAsync(int processId, int cpuLimit, CancellationToken cancellationToken)
	{
		string? taskset = _processes.FindExecutable("taskset");
		if (taskset is null)
		{
			return OperationResult.Fail("TasksetUnavailable", "The taskset utility is unavailable", CapabilityState.RequiresExternalRuntime);
		}

		string cores = "0" + (cpuLimit > 1 ? "-" + (cpuLimit - 1).ToString(CultureInfo.InvariantCulture) : string.Empty);
		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(
			new ProcessCommand(taskset, ["--cpu-list", cores, "--pid", processId.ToString(CultureInfo.InvariantCulture)]),
			cancellationToken);
		return ConvertCommandResult(result, "CpuAffinityFailed");
	}

	private async Task<OperationResult> ApplyNiceValueAsync(int processId, int niceValue, CancellationToken cancellationToken)
	{
		string? renice = _processes.FindExecutable("renice");
		if (renice is null)
		{
			return OperationResult.Fail("ReniceUnavailable", "The renice utility is unavailable", CapabilityState.RequiresExternalRuntime);
		}

		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(
			new ProcessCommand(renice, ["-n", niceValue.ToString(CultureInfo.InvariantCulture), "-p", processId.ToString(CultureInfo.InvariantCulture)]),
			cancellationToken);
		return ConvertCommandResult(result, "ProcessPriorityFailed");
	}

	private static int? GetNiceValue(ResourcePriority priority)
	{
		return priority switch
		{
			ResourcePriority.Idle => 19,
			ResourcePriority.BelowNormal => 10,
			_ => null
		};
	}

	private static OperationResult ConvertCommandResult(OperationResult<ProcessExecution> result, string failureCode)
	{
		if (!result.Succeeded || result.Value is null)
		{
			return CopyFailure(result.Failure);
		}

		if (result.Value.ExitCode == 0)
		{
			return OperationResult.Success();
		}

		string message = string.IsNullOrWhiteSpace(result.Value.StandardError)
			? "The system rejected the process resource change"
			: result.Value.StandardError.Trim();
		CapabilityState state = IsPermissionFailure(message) ? CapabilityState.RequiresPermission : CapabilityState.Unavailable;
		return OperationResult.Fail(failureCode, message, state);
	}

	private static bool IsPermissionFailure(string message)
	{
		return message.Contains("permission", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("operation not allowed", StringComparison.OrdinalIgnoreCase);
	}

	private static OperationResult CopyFailure(OperationFailure? failure)
	{
		return failure is null
			? OperationResult.Fail("ProcessResourceChangeFailed", "The process resource change failed")
			: OperationResult.Fail(failure.Code, failure.Message, failure.State);
	}

	private static OperationResult<T> CopyFailure<T>(OperationFailure? failure)
	{
		return failure is null
			? OperationResult<T>.Fail("ProcessResourceChangeFailed", "The process resource change failed")
			: OperationResult<T>.Fail(failure.Code, failure.Message, failure.State);
	}
}
