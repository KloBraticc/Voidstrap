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

	private static readonly object InstallGate = new();
	private static Task<OperationResult>? _activeInstall;

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
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["info", SoberApplicationId], out ProcessCommand command))
		{
			return new SoberInstallationState(SoberInstallationStatus.FlatpakMissing, null, FlatpakMissingMessage);
		}

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
		{
			return new SoberInstallationState(SoberInstallationStatus.NotInstalled, null, "Sober is not installed");
		}

		string? version = FlatpakApplicationInfo.ParseVersion(result.Value.StandardOutput);
		return new SoberInstallationState(
			SoberInstallationStatus.Installed,
			string.IsNullOrWhiteSpace(version) ? null : version,
			string.IsNullOrWhiteSpace(version) ? "Sober is installed" : "Sober " + version + " is installed");
	}

	public Task<OperationResult> InstallAsync(CancellationToken cancellationToken = default)
	{
		lock (InstallGate)
		{
			if (_activeInstall is { IsCompleted: false })
				return _activeInstall.WaitAsync(cancellationToken);

			_activeInstall = InstallOnceAsync(cancellationToken);
			return _activeInstall;
		}
	}

	public static bool IsInstallRunningElsewhere()
	{
		try
		{
			foreach (string directory in Directory.EnumerateDirectories("/proc"))
			{
				if (!int.TryParse(Path.GetFileName(directory), out int processId) || processId == Environment.ProcessId)
					continue;

				string commandLine;
				try
				{
					commandLine = File.ReadAllText(Path.Combine(directory, "cmdline"));
				}
				catch (Exception)
				{
					continue;
				}

				if (commandLine.Contains("flatpak", StringComparison.Ordinal)
					&& commandLine.Contains("\0install\0", StringComparison.Ordinal)
					&& (commandLine.Contains(SoberApplicationId, StringComparison.Ordinal) || commandLine.Contains(ReferenceUrl, StringComparison.Ordinal)))
				{
					return true;
				}
			}
		}
		catch (Exception)
		{
		}

		return false;
	}

	private async Task<OperationResult> InstallOnceAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, [], out _))
		{
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage, CapabilityState.RequiresExternalRuntime);
		}

		if (IsInstallRunningElsewhere())
		{
			while (IsInstallRunningElsewhere())
				await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

			if ((await DetectAsync(cancellationToken).ConfigureAwait(false)).Status == SoberInstallationStatus.Installed)
				return OperationResult.Success();
		}

		OperationResult remote = await RunAsync(
			["remote-add", "--if-not-exists", "--user", RemoteName, RemoteUrl],
			"FlathubRemoteFailed",
			"The Flathub repository could not be added",
			cancellationToken).ConfigureAwait(false);

		if (remote.Succeeded)
		{
			OperationResult fromRemote = await InstallTargetAsync([RemoteName, SoberApplicationId], cancellationToken).ConfigureAwait(false);
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
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["kill", SoberApplicationId], out ProcessCommand killCommand))
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
			["uninstall", "--user", "--assumeyes", "--noninteractive", "--delete-data", SoberApplicationId],
			"SoberUninstallFailed",
			"Sober could not be removed",
			cancellationToken).ConfigureAwait(false);

		if (user.Succeeded)
			return user;

		return await RunAsync(
			["uninstall", "--assumeyes", "--noninteractive", "--delete-data", SoberApplicationId],
			"SoberUninstallFailed",
			"Sober could not be removed",
			cancellationToken).ConfigureAwait(false);
	}

	private Task<OperationResult> InstallTargetAsync(IReadOnlyList<string> target, CancellationToken cancellationToken)
	{
		List<string> arguments = ["install", "--user", "--assumeyes", "--noninteractive", "--or-update"];
		arguments.AddRange(target);
		return RunAsync(
			arguments,
			"SoberInstallFailed",
			"Sober could not be installed",
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
