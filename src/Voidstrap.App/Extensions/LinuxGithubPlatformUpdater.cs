using Voidstrap.Core;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Extensions;

internal static class LinuxGithubPlatformUpdater
{
	private static string? _latestTag;

	public static IPlatformUpdater Create()
	{
		string? executable = Environment.ProcessPath;
		string? directory = executable is null ? null : Path.GetDirectoryName(executable);
		if (string.IsNullOrWhiteSpace(directory))
		{
			return new UnavailablePlatformUpdater(
				new CapabilityDescriptor(FeatureId.Updater, CapabilityState.Unavailable, "The current executable location is unavailable"));
		}

		if (LinuxBundleInstaller.IsPackageManagedLocation(directory))
		{
			return new UnavailablePlatformUpdater(
				new CapabilityDescriptor(
					FeatureId.Updater,
					CapabilityState.Unavailable,
					"Updates are managed by the Linux package manager",
					"Use the Linux package manager"));
		}

		return new LinuxPlatformUpdater(
			new CapabilityDescriptor(FeatureId.Updater, CapabilityState.Available, "Linux bundle updates are available"),
			CheckAsync,
			ApplyAsync);
	}

	private static async Task<OperationResult> CheckAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? tag = await GithubUpdater.GetLatestVersionTagAsync(cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(tag))
		{
			return OperationResult.Fail("UpdateCheckFailed", "The latest Linux release could not be resolved");
		}

		Volatile.Write(ref _latestTag, tag);
		return OperationResult.Success();
	}

	private static async Task<OperationResult> ApplyAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? tag = Volatile.Read(ref _latestTag);
		if (string.IsNullOrWhiteSpace(tag))
		{
			tag = await GithubUpdater.GetLatestVersionTagAsync(cancellationToken).ConfigureAwait(false);
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(tag))
		{
			return OperationResult.Fail("UpdateCheckFailed", "The latest Linux release could not be resolved");
		}

		bool installed = await GithubUpdater.DownloadAndInstallUpdate(tag, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		return installed
			? OperationResult.Success()
			: OperationResult.Fail("UpdateApplyFailed", "The Linux bundle update could not be installed");
	}
}
