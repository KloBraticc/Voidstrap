namespace Voidstrap.Platform.Linux;

public sealed record LinuxTrustStore(string Tool, string ToolPath, string AnchorDirectory)
{
	public string AnchorPath => Path.Combine(AnchorDirectory, LinuxCertificateTrust.AnchorFileName);
}

public sealed class LinuxCertificateTrust
{
	internal const string AnchorFileName = "voidstrap-assetwarp.crt";

	private static readonly (string Tool, string AnchorDirectory)[] KnownStores =
	[
		("update-ca-trust", "/etc/pki/ca-trust/source/anchors"),
		("update-ca-certificates", "/usr/local/share/ca-certificates")
	];

	private readonly IProcessService _processes;
	private readonly string _rootPrefix;

	public LinuxCertificateTrust(IProcessService processes)
		: this(processes, string.Empty)
	{
	}

	internal LinuxCertificateTrust(IProcessService processes, string rootPrefix)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
		_rootPrefix = rootPrefix ?? string.Empty;
	}

	public LinuxTrustStore? Detect()
	{
		foreach ((string tool, string anchorDirectory) in KnownStores)
		{
			string? toolPath = _processes.FindExecutable(tool);
			string resolved = ResolveAnchorDirectory(anchorDirectory);
			if (!string.IsNullOrWhiteSpace(toolPath) && Directory.Exists(resolved))
			{
				return new LinuxTrustStore(tool, toolPath, resolved);
			}
		}

		return null;
	}

	private string ResolveAnchorDirectory(string anchorDirectory)
	{
		return _rootPrefix.Length == 0
			? anchorDirectory
			: Path.Combine(_rootPrefix, anchorDirectory.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
	}

	public bool IsInstalled(string certificateFile)
	{
		LinuxTrustStore? store = Detect();
		if (store is null || !File.Exists(certificateFile) || !File.Exists(store.AnchorPath))
		{
			return false;
		}

		try
		{
			return File.ReadAllBytes(store.AnchorPath).AsSpan().SequenceEqual(File.ReadAllBytes(certificateFile));
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	public async Task<OperationResult> InstallAsync(string certificateFile, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(certificateFile) || !File.Exists(certificateFile))
		{
			return OperationResult.Fail("AssetWarpCertificateMissing", "The AssetWarp certificate has not been created yet");
		}

		LinuxTrustStore? store = Detect();
		if (store is null)
		{
			return OperationResult.Fail(
				"TrustStoreUnsupported",
				"No supported system certificate store was found",
				CapabilityState.Unavailable);
		}

		if (IsInstalled(certificateFile))
		{
			return OperationResult.Success();
		}

		return await RunPrivilegedAsync(
			"install -m 0644 \"$1\" \"$2\" && \"$3\"",
			[Path.GetFullPath(certificateFile), store.AnchorPath, store.ToolPath],
			"TrustStoreInstallFailed",
			"The AssetWarp certificate could not be added to the system trust store",
			cancellationToken).ConfigureAwait(false);
	}

	public async Task<OperationResult> RemoveAsync(CancellationToken cancellationToken = default)
	{
		LinuxTrustStore? store = Detect();
		if (store is null)
		{
			return OperationResult.Success();
		}

		if (!File.Exists(store.AnchorPath))
		{
			return OperationResult.Success();
		}

		return await RunPrivilegedAsync(
			"rm -f \"$1\" && \"$2\"",
			[store.AnchorPath, store.ToolPath],
			"TrustStoreRemoveFailed",
			"The AssetWarp certificate could not be removed from the system trust store",
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<OperationResult> RunPrivilegedAsync(
		string script,
		IReadOnlyList<string> scriptArguments,
		string failureCode,
		string failureMessage,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? pkexec = _processes.FindExecutable("pkexec");
		if (string.IsNullOrWhiteSpace(pkexec))
		{
			return OperationResult.Fail(
				"PolicyKitMissing",
				"The pkexec command is unavailable, so the certificate cannot be installed",
				CapabilityState.RequiresPermission);
		}

		List<string> arguments = ["/bin/sh", "-c", script, "voidstrap"];
		arguments.AddRange(scriptArguments);

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(new ProcessCommand(pkexec, arguments), cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null)
		{
			return OperationResult.Fail(failureCode, failureMessage);
		}

		if (result.Value.ExitCode == 126 || result.Value.ExitCode == 127)
		{
			return OperationResult.Fail(
				"TrustStoreAuthorizationDeclined",
				"The certificate change was not authorized",
				CapabilityState.RequiresPermission);
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
