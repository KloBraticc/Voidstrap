namespace Voidstrap.Platform.Linux;

public enum SoberInstallationStatus
{
	FlatpakMissing,
	NotInstalled,
	Installed
}

public sealed record SoberInstallationState(SoberInstallationStatus Status, string? Version, string Message);

public sealed class LinuxSoberInstaller
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";
	private const string RemoteName = "flathub";
	private const string RemoteUrl = "https://flathub.org/repo/flathub.flatpakrepo";
	private const string ReferenceUrl = "https://sober.vinegarhq.org/sober.flatpakref";
	private const string FlatpakMissingMessage = "Flatpak is not installed. Install Flatpak with your package manager, then try again.";

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

	public async Task<SoberInstallationState> DetectAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? flatpak = _processes.FindExecutable("flatpak");
		if (string.IsNullOrWhiteSpace(flatpak))
		{
			return new SoberInstallationState(SoberInstallationStatus.FlatpakMissing, null, FlatpakMissingMessage);
		}

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(new ProcessCommand(flatpak, ["info", "--show-version", SoberApplicationId]), cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
		{
			return new SoberInstallationState(SoberInstallationStatus.NotInstalled, null, "Sober is not installed");
		}

		string version = result.Value.StandardOutput.Trim();
		return new SoberInstallationState(
			SoberInstallationStatus.Installed,
			string.IsNullOrWhiteSpace(version) ? null : version,
			string.IsNullOrWhiteSpace(version) ? "Sober is installed" : "Sober " + version + " is installed");
	}

	public async Task<OperationResult> InstallAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? flatpak = _processes.FindExecutable("flatpak");
		if (string.IsNullOrWhiteSpace(flatpak))
		{
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage, CapabilityState.RequiresExternalRuntime);
		}

		OperationResult remote = await RunAsync(
			flatpak,
			["remote-add", "--if-not-exists", "--user", RemoteName, RemoteUrl],
			"FlathubRemoteFailed",
			"The Flathub repository could not be added",
			cancellationToken).ConfigureAwait(false);

		if (remote.Succeeded)
		{
			OperationResult fromRemote = await InstallTargetAsync(flatpak, [RemoteName, SoberApplicationId], cancellationToken).ConfigureAwait(false);
			if (fromRemote.Succeeded)
			{
				return fromRemote;
			}

			OperationResult fromReference = await InstallTargetAsync(flatpak, [ReferenceUrl], cancellationToken).ConfigureAwait(false);
			return fromReference.Succeeded ? fromReference : fromRemote;
		}

		OperationResult referenceOnly = await InstallTargetAsync(flatpak, [ReferenceUrl], cancellationToken).ConfigureAwait(false);
		return referenceOnly.Succeeded ? referenceOnly : remote;
	}

	private Task<OperationResult> InstallTargetAsync(string flatpak, IReadOnlyList<string> target, CancellationToken cancellationToken)
	{
		List<string> arguments = ["install", "--user", "--assumeyes", "--noninteractive", "--or-update"];
		arguments.AddRange(target);
		return RunAsync(
			flatpak,
			arguments,
			"SoberInstallFailed",
			"Sober could not be installed",
			cancellationToken);
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
			return result.Failure is null
				? OperationResult.Fail(failureCode, failureMessage)
				: OperationResult.Fail(failureCode, failureMessage + ": " + result.Failure.Message, result.Failure.State);
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
