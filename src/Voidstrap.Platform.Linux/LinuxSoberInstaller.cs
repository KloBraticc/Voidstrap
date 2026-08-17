namespace Voidstrap.Platform.Linux;

public sealed class LinuxSoberInstaller
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";
	private const string RemoteName = "flathub";
	private const string RemoteUrl = "https://flathub.org/repo/flathub.flatpakrepo";

	private readonly IProcessService _processes;

	public LinuxSoberInstaller(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	public static bool CanInstall(CapabilityDescriptor capability)
	{
		ArgumentNullException.ThrowIfNull(capability);
		return capability.State == CapabilityState.RequiresExternalRuntime;
	}

	public async Task<OperationResult> InstallAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? flatpak = _processes.FindExecutable("flatpak");
		if (string.IsNullOrWhiteSpace(flatpak))
		{
			return OperationResult.Fail(
				"FlatpakMissing",
				"Flatpak is not installed. Install Flatpak with your package manager, then try again.",
				CapabilityState.RequiresExternalRuntime);
		}

		OperationResult remote = await RunAsync(
			flatpak,
			["remote-add", "--if-not-exists", "--user", RemoteName, RemoteUrl],
			"FlathubRemoteFailed",
			"The Flathub repository could not be added",
			cancellationToken).ConfigureAwait(false);
		if (!remote.Succeeded)
		{
			return remote;
		}

		return await RunAsync(
			flatpak,
			["install", "--user", "--assumeyes", "--noninteractive", RemoteName, SoberApplicationId],
			"SoberInstallFailed",
			"Sober could not be installed",
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<OperationResult> RunAsync(
		string flatpak,
		IReadOnlyList<string> arguments,
		string failureCode,
		string failureMessage,
		CancellationToken cancellationToken)
	{
		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(new ProcessCommand(flatpak, arguments), cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null)
		{
			return OperationResult.Fail(failureCode, failureMessage);
		}

		if (result.Value.ExitCode != 0)
		{
			string detail = string.IsNullOrWhiteSpace(result.Value.StandardError)
				? result.Value.StandardOutput
				: result.Value.StandardError;
			return string.IsNullOrWhiteSpace(detail)
				? OperationResult.Fail(failureCode, failureMessage)
				: OperationResult.Fail(failureCode, failureMessage + ": " + detail.Trim());
		}

		return OperationResult.Success();
	}
}
