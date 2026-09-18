using System.Diagnostics;

namespace Voidstrap.Platform.Linux;

public enum VinegarRenderer
{
	Dxvk,
	DxvkSarek,
	Vulkan,
	D3D11,
	D3D11FL10
}

public static class VinegarRendererValues
{
	public static string ToConfigValue(this VinegarRenderer renderer)
	{
		return renderer switch
		{
			VinegarRenderer.Dxvk => "DXVK",
			VinegarRenderer.DxvkSarek => "DXVK-Sarek",
			VinegarRenderer.Vulkan => "Vulkan",
			VinegarRenderer.D3D11 => "D3D11",
			VinegarRenderer.D3D11FL10 => "D3D11FL10",
			_ => "DXVK"
		};
	}

	public static VinegarRenderer? FromConfigValue(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;

		return value.Trim() switch
		{
			"DXVK" => VinegarRenderer.Dxvk,
			"DXVK-Sarek" => VinegarRenderer.DxvkSarek,
			"Vulkan" => VinegarRenderer.Vulkan,
			"D3D11" => VinegarRenderer.D3D11,
			"D3D11FL10" => VinegarRenderer.D3D11FL10,
			_ => null
		};
	}
}

public sealed record VinegarNativeConfigurationOptions(
	VinegarRenderer? Renderer = null,
	bool? EnableGameMode = null,
	bool? DiscordRpcEnabled = null,
	string? Gpu = null,
	string? VirtualDesktop = null,
	string? Launcher = null,
	string? ForcedVersion = null,
	string? Channel = null,
	string? WineRoot = null);

public sealed record LinuxStudioPreparationOptions(
	bool UseFastFlagManager = true,
	VinegarNativeConfigurationOptions? NativeConfiguration = null,
	bool ApplyModifications = true,
	IReadOnlyList<LinuxModSource>? AdditionalModSources = null);

public interface IVinegarProcessProbe
{
	Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);
}

public sealed class LinuxVinegarProcessProbe : IVinegarProcessProbe
{
	private const string VinegarApplicationId = "org.vinegarhq.Vinegar";

	private readonly IProcessService _processes;

	public LinuxVinegarProcessProbe(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (HasStudioProcess())
			return true;

		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["ps", "--columns=application"], out ProcessCommand command))
			return false;

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
			return false;

		foreach (string line in result.Value.StandardOutput.Split('\n'))
		{
			if (string.Equals(line.Trim(), VinegarApplicationId, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	public static bool HasStudioProcess()
	{
		return StudioProcessNames.AnyRunning();
	}
}

public static class StudioProcessNames
{
	private const string Prefix = "RobloxStudio";

	public static bool IsStudio(string? processName)
	{
		return !string.IsNullOrEmpty(processName)
			&& processName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
	}

	public static bool AnyRunning()
	{
		Process[] processes;
		try
		{
			processes = Process.GetProcesses();
		}
		catch (Exception)
		{
			return false;
		}

		bool found = false;
		foreach (Process process in processes)
		{
			if (!found)
			{
				try
				{
					found = IsStudio(process.ProcessName);
				}
				catch (Exception)
				{
				}
			}

			try
			{
				process.Dispose();
			}
			catch (Exception)
			{
			}
		}

		return found;
	}
}
