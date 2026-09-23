using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Voidstrap.Core;
using Voidstrap.Platform;

namespace Voidstrap.Platform.Linux;

public enum LinuxInstallationKind
{
	PortableBundle,
	AppImage,
	Flatpak,
	Debian,
	Rpm,
	Arch,
	ManagedUnknown
}

public enum LinuxPackageScope
{
	Unknown,
	User,
	System
}

public sealed record LinuxInstallationInfo(
	LinuxInstallationKind Kind,
	string ExecutablePath,
	string PackageName,
	LinuxPackageScope Scope);

public static partial class LinuxInstallationUpdates
{
	public const string ApplicationId = "io.github.KloBraticc.Voidstrap";

	private static readonly HashSet<string> PackageNames = new(StringComparer.Ordinal)
	{
		"voidstrap",
		"voidstrap-bin",
		"voidstrap-git"
	};

	[LibraryImport("libc", EntryPoint = "geteuid")]
	private static partial uint GetEffectiveUserId();

	public static async Task<LinuxInstallationInfo> DetectAsync(
		IProcessService processes,
		string? executablePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		if (!OperatingSystem.IsLinux())
			throw new PlatformNotSupportedException("Linux installation detection requires Linux");

		cancellationToken.ThrowIfCancellationRequested();
		if (LinuxAppImageHost.TryGetCurrentPath(out string appImagePath))
			return new LinuxInstallationInfo(LinuxInstallationKind.AppImage, appImagePath, string.Empty, LinuxPackageScope.User);

		string resolvedExecutable = ResolveExecutable(executablePath);
		if (LinuxFlatpakHost.IsSandboxed)
		{
			string packageName = LinuxFlatpakHost.CurrentApplicationId;
			LinuxPackageScope scope = await DetectFlatpakScopeAsync(processes, packageName, cancellationToken).ConfigureAwait(false);
			return new LinuxInstallationInfo(LinuxInstallationKind.Flatpak, resolvedExecutable, packageName, scope);
		}

		string? debianPackage = await QueryPackageOwnerAsync(processes, "dpkg-query", ["-S", resolvedExecutable], ParseDebianOwner, cancellationToken).ConfigureAwait(false);
		if (debianPackage is not null)
			return new LinuxInstallationInfo(LinuxInstallationKind.Debian, resolvedExecutable, debianPackage, LinuxPackageScope.System);

		string? rpmPackage = await QueryPackageOwnerAsync(processes, "rpm", ["-qf", "--queryformat", "%{NAME}\n", resolvedExecutable], ParseSinglePackageName, cancellationToken).ConfigureAwait(false);
		if (rpmPackage is not null)
			return new LinuxInstallationInfo(LinuxInstallationKind.Rpm, resolvedExecutable, rpmPackage, LinuxPackageScope.System);

		string? archPackage = await QueryPackageOwnerAsync(processes, "pacman", ["-Qqo", resolvedExecutable], ParseSinglePackageName, cancellationToken).ConfigureAwait(false);
		if (archPackage is not null)
			return new LinuxInstallationInfo(LinuxInstallationKind.Arch, resolvedExecutable, archPackage, LinuxPackageScope.System);

		string? directory = Path.GetDirectoryName(resolvedExecutable);
		return new LinuxInstallationInfo(
			!string.IsNullOrWhiteSpace(directory) && LinuxBundleInstaller.IsPackageManagedLocation(directory)
				? LinuxInstallationKind.ManagedUnknown
				: LinuxInstallationKind.PortableBundle,
			resolvedExecutable,
			string.Empty,
			LinuxPackageScope.Unknown);
	}

	public static async Task<OperationResult> InstallPackageAsync(
		IProcessService processes,
		LinuxInstallationInfo installation,
		string packagePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentNullException.ThrowIfNull(installation);
		ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
		cancellationToken.ThrowIfCancellationRequested();

		string fullPackagePath = Path.GetFullPath(packagePath);
		if (!File.Exists(fullPackagePath) || new FileInfo(fullPackagePath).Length == 0)
			return OperationResult.Fail("PackageUnavailable", "The downloaded Linux package is unavailable");

		return installation.Kind switch
		{
			LinuxInstallationKind.Flatpak => await InstallFlatpakAsync(processes, installation, fullPackagePath, cancellationToken).ConfigureAwait(false),
			LinuxInstallationKind.Debian => await InstallDebianAsync(processes, fullPackagePath, cancellationToken).ConfigureAwait(false),
			LinuxInstallationKind.Rpm => await InstallRpmAsync(processes, fullPackagePath, cancellationToken).ConfigureAwait(false),
			_ => OperationResult.Fail("UnsupportedPackage", "The detected Linux installation cannot use this package")
		};
	}

	public static async Task<OperationResult> UpdateFromPackageSourceAsync(
		IProcessService processes,
		LinuxInstallationInfo installation,
		string expectedVersionTag,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentNullException.ThrowIfNull(installation);
		cancellationToken.ThrowIfCancellationRequested();

		OperationResult updateResult = installation.Kind switch
		{
			LinuxInstallationKind.Flatpak => await UpdateFlatpakSourceAsync(processes, installation, cancellationToken).ConfigureAwait(false),
			LinuxInstallationKind.Debian => await UpdateDebianSourceAsync(processes, installation.PackageName, cancellationToken).ConfigureAwait(false),
			LinuxInstallationKind.Rpm => await UpdateRpmSourceAsync(processes, installation.PackageName, cancellationToken).ConfigureAwait(false),
			LinuxInstallationKind.Arch => await UpdateArchSourceAsync(processes, installation.PackageName, cancellationToken).ConfigureAwait(false),
			LinuxInstallationKind.ManagedUnknown => OperationResult.Fail("UnknownPackageManager", "The package manager that owns this Voidstrap installation could not be identified"),
			_ => OperationResult.Fail("NoPackageSource", "This Voidstrap installation has no package source update path")
		};
		if (!updateResult.Succeeded)
			return updateResult;
		return await HasExpectedVersionAsync(processes, installation, expectedVersionTag, cancellationToken).ConfigureAwait(false)
			? OperationResult.Success()
			: OperationResult.Fail("PackageSourceBehind", "The installed package source does not contain this Voidstrap release yet");
	}

	public static async Task<bool> HasExpectedVersionAsync(
		IProcessService processes,
		LinuxInstallationInfo installation,
		string expectedVersionTag,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentNullException.ThrowIfNull(installation);
		if (!Version.TryParse(expectedVersionTag.Trim().TrimStart('v', 'V'), out Version? expectedVersion))
			return false;

		ProcessCommand? command = CreateVersionQuery(processes, installation);
		if (command is null)
			return false;
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded || result.Value is not { ExitCode: 0 } execution)
			return false;
		MatchCollection matches = VersionNumberPattern.Matches(execution.StandardOutput);
		foreach (Match match in matches)
		{
			if (Version.TryParse(match.Groups[1].Value, out Version? installedVersion) && installedVersion >= expectedVersion)
				return true;
		}
		return false;
	}

	private static string ResolveExecutable(string? executablePath)
	{
		string path = string.IsNullOrWhiteSpace(executablePath)
			? Environment.ProcessPath ?? string.Empty
			: executablePath;
		if (string.IsNullOrWhiteSpace(path))
			throw new InvalidOperationException("The current executable location is unavailable");
		return Path.GetFullPath(path);
	}

	private static async Task<LinuxPackageScope> DetectFlatpakScopeAsync(
		IProcessService processes,
		string applicationId,
		CancellationToken cancellationToken)
	{
		if (await RunFlatpakAsync(processes, ["info", "--user", applicationId], cancellationToken).ConfigureAwait(false))
			return LinuxPackageScope.User;
		if (await RunFlatpakAsync(processes, ["info", "--system", applicationId], cancellationToken).ConfigureAwait(false))
			return LinuxPackageScope.System;
		return LinuxPackageScope.Unknown;
	}

	private static ProcessCommand? CreateVersionQuery(IProcessService processes, LinuxInstallationInfo installation)
	{
		if (installation.Kind == LinuxInstallationKind.Flatpak)
		{
			List<string> flatpakArguments = ["info"];
			flatpakArguments.Add(installation.Scope == LinuxPackageScope.System ? "--system" : "--user");
			flatpakArguments.AddRange(["--show-version", installation.PackageName]);
			return LinuxFlatpakHost.TryCreateCommand(processes, flatpakArguments, out ProcessCommand command) ? command : null;
		}

		string commandName;
		IReadOnlyList<string> arguments;
		switch (installation.Kind)
		{
			case LinuxInstallationKind.Debian:
				commandName = "dpkg-query";
				arguments = ["-W", "-f=${Version}\n", installation.PackageName];
				break;
			case LinuxInstallationKind.Rpm:
				commandName = "rpm";
				arguments = ["-q", "--queryformat", "%{VERSION}\n", installation.PackageName];
				break;
			case LinuxInstallationKind.Arch:
				commandName = "pacman";
				arguments = ["-Q", installation.PackageName];
				break;
			default:
				return null;
		}
		string? executable = processes.FindExecutable(commandName);
		return string.IsNullOrWhiteSpace(executable) ? null : new ProcessCommand(executable, arguments);
	}

	private static async Task<string?> QueryPackageOwnerAsync(
		IProcessService processes,
		string commandName,
		IReadOnlyList<string> arguments,
		Func<string, string?> parser,
		CancellationToken cancellationToken)
	{
		string? executable = processes.FindExecutable(commandName);
		if (string.IsNullOrWhiteSpace(executable))
			return null;
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(new ProcessCommand(executable, arguments), cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded || result.Value is not { ExitCode: 0 } execution)
			return null;
		string? packageName = parser(execution.StandardOutput);
		return packageName is not null && PackageNames.Contains(packageName) ? packageName : null;
	}

	private static string? ParseDebianOwner(string output)
	{
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			int separator = line.IndexOf(": ", StringComparison.Ordinal);
			if (separator <= 0)
				continue;
			string packageName = line[..separator];
			int architectureSeparator = packageName.IndexOf(':');
			if (architectureSeparator > 0)
				packageName = packageName[..architectureSeparator];
			if (PackageNames.Contains(packageName))
				return packageName;
		}
		return null;
	}

	private static string? ParseSinglePackageName(string output)
	{
		string packageName = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
		return PackageNames.Contains(packageName) ? packageName : null;
	}

	private static async Task<OperationResult> InstallFlatpakAsync(
		IProcessService processes,
		LinuxInstallationInfo installation,
		string packagePath,
		CancellationToken cancellationToken)
	{
		if (installation.Scope == LinuxPackageScope.Unknown)
			return OperationResult.Fail("FlatpakScopeUnknown", "The current Flatpak installation scope could not be identified");
		List<string> arguments = ["install"];
		arguments.Add(installation.Scope == LinuxPackageScope.System ? "--system" : "--user");
		arguments.AddRange(["--bundle", "--or-update", "--noninteractive", "--assumeyes", packagePath]);
		return await RunFlatpakResultAsync(processes, arguments, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<OperationResult> UpdateFlatpakSourceAsync(
		IProcessService processes,
		LinuxInstallationInfo installation,
		CancellationToken cancellationToken)
	{
		if (installation.Scope == LinuxPackageScope.Unknown)
			return OperationResult.Fail("FlatpakScopeUnknown", "The current Flatpak installation scope could not be identified");
		List<string> arguments = ["update"];
		arguments.Add(installation.Scope == LinuxPackageScope.System ? "--system" : "--user");
		arguments.AddRange(["--noninteractive", "--assumeyes", installation.PackageName]);
		return await RunFlatpakResultAsync(processes, arguments, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<bool> RunFlatpakAsync(IProcessService processes, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		OperationResult result = await RunFlatpakResultAsync(processes, arguments, cancellationToken).ConfigureAwait(false);
		return result.Succeeded;
	}

	private static async Task<OperationResult> RunFlatpakResultAsync(IProcessService processes, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		if (!LinuxFlatpakHost.TryCreateCommand(processes, arguments, out ProcessCommand command))
			return OperationResult.Fail("FlatpakUnavailable", "Flatpak could not be started");
		return await ExecuteCommandsAsync(processes, [command], cancellationToken).ConfigureAwait(false);
	}

	private static async Task<OperationResult> InstallDebianAsync(IProcessService processes, string packagePath, CancellationToken cancellationToken)
	{
		List<ProcessCommand> commands = [];
		AddElevatedCommand(processes, commands, "apt-get", ["install", "--yes", "--no-remove", packagePath]);
		AddElevatedCommand(processes, commands, "apt", ["install", "--yes", packagePath]);
		AddElevatedCommand(processes, commands, "dpkg", ["--install", packagePath]);
		return await ExecuteCommandsAsync(processes, commands, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<OperationResult> UpdateDebianSourceAsync(IProcessService processes, string packageName, CancellationToken cancellationToken)
	{
		List<ProcessCommand> commands = [];
		AddElevatedCommand(processes, commands, "apt-get", ["install", "--only-upgrade", "--yes", "--no-remove", packageName]);
		AddElevatedCommand(processes, commands, "apt", ["install", "--only-upgrade", "--yes", packageName]);
		return await ExecuteCommandsAsync(processes, commands, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<OperationResult> InstallRpmAsync(IProcessService processes, string packagePath, CancellationToken cancellationToken)
	{
		List<ProcessCommand> commands = [];
		AddElevatedCommand(processes, commands, "dnf5", ["install", "--assumeyes", packagePath]);
		AddElevatedCommand(processes, commands, "dnf", ["install", "--assumeyes", packagePath]);
		AddElevatedCommand(processes, commands, "yum", ["install", "--assumeyes", packagePath]);
		AddElevatedCommand(processes, commands, "zypper", ["--non-interactive", "install", packagePath]);
		AddElevatedCommand(processes, commands, "dnf5", ["install", "--assumeyes", "--nogpgcheck", packagePath]);
		AddElevatedCommand(processes, commands, "dnf", ["install", "--assumeyes", "--nogpgcheck", packagePath]);
		AddElevatedCommand(processes, commands, "rpm", ["--upgrade", "--replacepkgs", packagePath]);
		return await ExecuteCommandsAsync(processes, commands, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<OperationResult> UpdateRpmSourceAsync(IProcessService processes, string packageName, CancellationToken cancellationToken)
	{
		List<ProcessCommand> commands = [];
		AddElevatedCommand(processes, commands, "dnf5", ["upgrade", "--assumeyes", packageName]);
		AddElevatedCommand(processes, commands, "dnf", ["upgrade", "--assumeyes", packageName]);
		AddElevatedCommand(processes, commands, "yum", ["update", "--assumeyes", packageName]);
		AddElevatedCommand(processes, commands, "zypper", ["--non-interactive", "update", packageName]);
		return await ExecuteCommandsAsync(processes, commands, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<OperationResult> UpdateArchSourceAsync(IProcessService processes, string packageName, CancellationToken cancellationToken)
	{
		List<ProcessCommand> commands = [];
		string? pkexec = processes.FindExecutable("pkexec");
		string[] escalation = string.IsNullOrWhiteSpace(pkexec) ? [] : ["--sudo", pkexec];
		AddCommand(processes, commands, "paru", ["--sync", "--needed", "--noconfirm", "--skipreview", .. escalation, packageName]);
		AddCommand(processes, commands, "yay", ["--sync", "--needed", "--noconfirm", .. escalation, packageName]);
		AddCommand(processes, commands, "pamac", ["build", "--no-confirm", packageName]);
		OperationResult helperResult = await ExecuteCommandsAsync(processes, commands, cancellationToken).ConfigureAwait(false);
		if (helperResult.Succeeded)
			return helperResult;

		string? git = processes.FindExecutable("git");
		string? makepkg = processes.FindExecutable("makepkg");
		if (string.IsNullOrWhiteSpace(git) || string.IsNullOrWhiteSpace(makepkg))
			return helperResult;

		string temporaryRoot = Path.Combine(Path.GetTempPath(), "Voidstrap_Aur_Update_" + Guid.NewGuid().ToString("N"));
		string checkout = Path.Combine(temporaryRoot, packageName);
		Directory.CreateDirectory(temporaryRoot);
		try
		{
			OperationResult<ProcessExecution> clone = await processes.ExecuteAsync(
				new ProcessCommand(git, ["clone", "--depth", "1", "https://aur.archlinux.org/" + packageName + ".git", checkout]),
				cancellationToken).ConfigureAwait(false);
			if (!clone.Succeeded || clone.Value is not { ExitCode: 0 })
				return FailureFromExecution("AurCheckoutFailed", "The AUR update recipe could not be downloaded", clone);

			OperationResult<ProcessExecution> build = await processes.ExecuteAsync(
				new ProcessCommand(makepkg, ["--syncdeps", "--install", "--needed", "--noconfirm", "--clean"], WorkingDirectory: checkout),
				cancellationToken).ConfigureAwait(false);
			return build.Succeeded && build.Value is { ExitCode: 0 }
				? OperationResult.Success()
				: FailureFromExecution("AurUpdateFailed", "The AUR package update did not complete", build);
		}
		finally
		{
			try
			{
				if (Directory.Exists(temporaryRoot))
					Directory.Delete(temporaryRoot, true);
			}
			catch
			{
			}
		}
	}

	private static void AddElevatedCommand(IProcessService processes, List<ProcessCommand> commands, string name, IReadOnlyList<string> arguments)
	{
		string? executable = processes.FindExecutable(name);
		if (string.IsNullOrWhiteSpace(executable))
			return;
		if (GetEffectiveUserId() == 0)
		{
			commands.Add(new ProcessCommand(executable, arguments));
			return;
		}
		string? elevation = processes.FindExecutable("pkexec");
		if (!string.IsNullOrWhiteSpace(elevation))
		{
			List<string> elevatedArguments = [executable];
			elevatedArguments.AddRange(arguments);
			commands.Add(new ProcessCommand(elevation, elevatedArguments));
		}
		string? sudo = processes.FindExecutable("sudo");
		if (!string.IsNullOrWhiteSpace(sudo))
		{
			List<string> sudoArguments = ["--non-interactive", executable];
			sudoArguments.AddRange(arguments);
			commands.Add(new ProcessCommand(sudo, sudoArguments));
		}
		string? doas = processes.FindExecutable("doas");
		if (!string.IsNullOrWhiteSpace(doas))
		{
			List<string> doasArguments = ["-n", executable];
			doasArguments.AddRange(arguments);
			commands.Add(new ProcessCommand(doas, doasArguments));
		}
	}

	private static void AddCommand(IProcessService processes, List<ProcessCommand> commands, string name, IReadOnlyList<string> arguments)
	{
		string? executable = processes.FindExecutable(name);
		if (!string.IsNullOrWhiteSpace(executable))
			commands.Add(new ProcessCommand(executable, arguments));
	}

	private static async Task<OperationResult> ExecuteCommandsAsync(
		IProcessService processes,
		List<ProcessCommand> commands,
		CancellationToken cancellationToken)
	{
		if (commands.Count == 0)
			return OperationResult.Fail("PackageToolUnavailable", "No compatible Linux package tool is available");

		OperationResult<ProcessExecution>? lastResult = null;
		foreach (ProcessCommand command in commands)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lastResult = await processes.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
			if (lastResult.Succeeded && lastResult.Value is { ExitCode: 0 })
				return OperationResult.Success();
		}
		return FailureFromExecution("PackageUpdateFailed", "The Linux package manager could not complete the update", lastResult);
	}

	private static OperationResult FailureFromExecution(
		string code,
		string fallbackMessage,
		OperationResult<ProcessExecution>? result)
	{
		string detail = result?.Value?.StandardError?.Trim() ?? result?.Failure?.Message ?? string.Empty;
		if (string.IsNullOrWhiteSpace(detail))
			detail = result?.Value?.StandardOutput?.Trim() ?? string.Empty;
		if (detail.Length > 1024)
			detail = detail[..1024];
		return OperationResult.Fail(code, string.IsNullOrWhiteSpace(detail) ? fallbackMessage : detail);
	}

    [GeneratedRegex(@"(?<![0-9])([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberPattern { get; }
}
