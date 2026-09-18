using System.Globalization;
using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public static class LinuxGamescope
{
	public const string LayerId = "org.freedesktop.Platform.VulkanLayer.gamescope";

	private const string SoberApplicationId = "org.vinegarhq.Sober";

	private const string SandboxBinaryDirectory = "/usr/lib/extensions/vulkan/gamescope/bin";

	private const string SandboxLibraryDirectory = "/usr/lib/extensions/vulkan/gamescope/lib";

	private const string WaylandDisplay = "gamescope-0";

	public static string LauncherPath => SandboxBinaryDirectory + "/gamescope";

	public static bool IsInstalled()
	{
		return LinuxEffectLayers.IsInstalled(LayerId);
	}

	public static IReadOnlyList<string> BuildCompositorArguments(int width, int height)
	{
		List<string> arguments = ["--backend", "sdl", "-f", "--force-grab-cursor"];

		if (width > 0 && height > 0)
		{
			arguments.Add("-W");
			arguments.Add(width.ToString(CultureInfo.InvariantCulture));
			arguments.Add("-H");
			arguments.Add(height.ToString(CultureInfo.InvariantCulture));
		}

		arguments.Add("--");
		arguments.Add("sober");
		return arguments;
	}

	public static async Task<OperationResult> ApplyLookAsync(
		IProcessService processes,
		string lutFile,
		CancellationToken cancellationToken = default)
	{
		if (processes is null)
			return OperationResult.Fail("GamescopeControlUnavailable", "No process service is available");

		OperationResult<string> instance = await FindInstanceAsync(processes, cancellationToken).ConfigureAwait(false);
		if (!instance.Succeeded || string.IsNullOrEmpty(instance.Value))
			return OperationResult.Fail("GamescopeNotRunning", "Roblox is not running inside the compositor");

		string command = "PATH=" + SandboxBinaryDirectory + ":$PATH"
			+ " LD_LIBRARY_PATH=" + SandboxLibraryDirectory + ":$LD_LIBRARY_PATH"
			+ " GAMESCOPE_WAYLAND_DISPLAY=" + WaylandDisplay
			+ " gamescopectl set_look " + Quote(lutFile);

		if (!LinuxFlatpakHost.TryCreateCommand(processes, ["enter", instance.Value!, "sh", "-c", command], out ProcessCommand flatpakCommand))
			return OperationResult.Fail("FlatpakMissing", "Flatpak is not installed");

		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(
			flatpakCommand,
			cancellationToken).ConfigureAwait(false);

		if (!result.Succeeded || result.Value is null)
			return OperationResult.Fail("GamescopeControlFailed", result.Failure?.Message ?? "The look could not be applied");

		return result.Value.ExitCode == 0
			? OperationResult.Success()
			: OperationResult.Fail("GamescopeControlFailed", "The compositor rejected the look");
	}

	private static async Task<OperationResult<string>> FindInstanceAsync(
		IProcessService processes,
		CancellationToken cancellationToken)
	{
		if (!LinuxFlatpakHost.TryCreateCommand(processes, ["ps", "--columns=instance,application"], out ProcessCommand flatpakCommand))
			return OperationResult<string>.Fail("FlatpakMissing", "Flatpak is not installed");

		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(
			flatpakCommand,
			cancellationToken).ConfigureAwait(false);

		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
			return OperationResult<string>.Fail("FlatpakQueryFailed", "The running applications could not be listed");

		foreach (string line in result.Value.StandardOutput.Split('\n'))
		{
			if (line.IndexOf(SoberApplicationId, StringComparison.Ordinal) < 0)
				continue;

			string instance = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
			if (instance.Length > 0)
				return OperationResult<string>.Success(instance);
		}

		return OperationResult<string>.Fail("GamescopeNotRunning", "Roblox is not running");
	}

	private static string Quote(string value)
	{
		return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
	}
}
