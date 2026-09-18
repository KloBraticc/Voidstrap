namespace Voidstrap.Platform.Linux;

public enum VinegarInstallationStatus
{
	FlatpakMissing,
	NotInstalled,
	Installed
}

public sealed record VinegarInstallationState(VinegarInstallationStatus Status, string? Version, string Message);

public sealed class LinuxVinegarInstaller
{
	private const string VinegarApplicationId = "org.vinegarhq.Vinegar";
	private const string RemoteName = "flathub";
	private const string RemoteUrl = "https://flathub.org/repo/flathub.flatpakrepo";
	private const string ReferenceUrl = "https://dl.flathub.org/repo/appstream/org.vinegarhq.Vinegar.flatpakref";
	private const string FlatpakMissingMessage = "Flatpak is not installed. Install Flatpak with your package manager, then try again.";

	private readonly IProcessService _processes;

	public LinuxVinegarInstaller(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	public static bool CanInstall(CapabilityDescriptor capability)
	{
		ArgumentNullException.ThrowIfNull(capability);
		return capability.State == CapabilityState.RequiresExternalRuntime;
	}

	public async Task<VinegarInstallationState> DetectAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["info", VinegarApplicationId], out ProcessCommand command))
		{
			return new VinegarInstallationState(VinegarInstallationStatus.FlatpakMissing, null, FlatpakMissingMessage);
		}

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
		{
			return new VinegarInstallationState(VinegarInstallationStatus.NotInstalled, null, "Vinegar is not installed");
		}

		string? version = FlatpakApplicationInfo.ParseVersion(result.Value.StandardOutput);
		return new VinegarInstallationState(
			VinegarInstallationStatus.Installed,
			string.IsNullOrWhiteSpace(version) ? null : version,
			string.IsNullOrWhiteSpace(version) ? "Vinegar is installed" : "Vinegar " + version + " is installed");
	}

	public async Task<OperationResult> InstallAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, [], out _))
		{
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage, CapabilityState.RequiresExternalRuntime);
		}

		OperationResult remote = await RunAsync(
			["remote-add", "--if-not-exists", "--user", RemoteName, RemoteUrl],
			"FlathubRemoteFailed",
			"The Flathub repository could not be added",
			cancellationToken).ConfigureAwait(false);

		if (remote.Succeeded)
		{
			OperationResult fromRemote = await InstallTargetAsync([RemoteName, VinegarApplicationId], cancellationToken).ConfigureAwait(false);
			if (fromRemote.Succeeded)
			{
				return fromRemote;
			}

			OperationResult fromReference = await InstallTargetAsync([ReferenceUrl], cancellationToken).ConfigureAwait(false);
			return fromReference.Succeeded ? fromReference : fromRemote;
		}

		OperationResult referenceOnly = await InstallTargetAsync([ReferenceUrl], cancellationToken).ConfigureAwait(false);
		return referenceOnly.Succeeded ? referenceOnly : remote;
	}

	public async Task<OperationResult> UninstallAsync(CancellationToken cancellationToken = default)
	{
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["kill", VinegarApplicationId], out ProcessCommand killCommand))
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage);

		try
		{
			await _processes
				.ExecuteAsync(killCommand, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception)
		{
		}

		OperationResult user = await RunAsync(
			["uninstall", "--user", "--assumeyes", "--noninteractive", "--delete-data", VinegarApplicationId],
			"VinegarUninstallFailed",
			"Vinegar could not be removed",
			cancellationToken).ConfigureAwait(false);

		if (user.Succeeded)
			return user;

		return await RunAsync(
			["uninstall", "--assumeyes", "--noninteractive", "--delete-data", VinegarApplicationId],
			"VinegarUninstallFailed",
			"Vinegar could not be removed",
			cancellationToken).ConfigureAwait(false);
	}

	private Task<OperationResult> InstallTargetAsync(IReadOnlyList<string> target, CancellationToken cancellationToken)
	{
		List<string> arguments = ["install", "--user", "--assumeyes", "--noninteractive", "--or-update"];
		arguments.AddRange(target);
		return RunAsync(
			arguments,
			"VinegarInstallFailed",
			"Vinegar could not be installed",
			cancellationToken);
	}

	private async Task<OperationResult> RunAsync(
		IReadOnlyList<string> arguments,
		string failureCode,
		string failureMessage,
		CancellationToken cancellationToken)
	{
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, arguments, out ProcessCommand command))
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage, CapabilityState.RequiresExternalRuntime);

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
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
