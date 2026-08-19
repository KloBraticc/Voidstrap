namespace Voidstrap.Platform.Linux;

public enum SoberGraphicsOptimizationMode
{
	Quality,
	Balanced,
	Performance
}

public enum SoberTouchMode
{
	Off,
	On,
	FakeOff
}

public sealed record SoberNativeConfigurationOptions(
	bool? AllowGamepadPermission = null,
	bool? CloseOnLeave = null,
	bool? DiscordRpcEnabled = null,
	bool? DiscordRpcShowJoinButton = null,
	bool? EnableGameMode = null,
	bool? EnableHiDpi = null,
	bool? EnableMobileHomeScreen = null,
	SoberGraphicsOptimizationMode? GraphicsOptimizationMode = null,
	bool? ServerLocationIndicatorEnabled = null,
	SoberTouchMode? TouchMode = null,
	bool? UseConsoleExperience = null,
	bool? UseLibsecret = null,
	bool? UseOpenGl = null);

public sealed record LinuxModSource(string RelativePath, string SourcePath);

public sealed record LinuxPlayerPreparationOptions(
	bool UseFastFlagManager = true,
	SoberNativeConfigurationOptions? NativeConfiguration = null,
	bool ApplyModifications = true,
	IReadOnlyList<LinuxModSource>? AdditionalModSources = null);

public interface ISoberProcessProbe
{
	Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);
}

public sealed class LinuxSoberProcessProbe : ISoberProcessProbe
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";

	private readonly IProcessService _processes;

	public LinuxSoberProcessProbe(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? flatpak = _processes.FindExecutable("flatpak");
		if (string.IsNullOrWhiteSpace(flatpak))
			return false;

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(new ProcessCommand(flatpak, ["ps", "--columns=application"]), cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
			return false;

		foreach (string line in result.Value.StandardOutput.Split('\n'))
		{
			if (string.Equals(line.Trim(), SoberApplicationId, StringComparison.Ordinal))
				return true;
		}

		return false;
	}
}
