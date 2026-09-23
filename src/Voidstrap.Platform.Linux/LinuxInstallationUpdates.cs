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

	public const string AuthorizationCancelledCode = "AuthorizationCancelled";

	public const string ArchRecipeAssetName = "Voidstrap_AUR_metadata.tar.gz";

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
		string output = installation.Kind == LinuxInstallationKind.Flatpak
			? string.Join('\n', execution.StandardOutput.Split('\n').Where(line => line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == installation.PackageName))
			: execution.StandardOutput;
		foreach (Match match in VersionNumberPattern.Matches(output))
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
			List<string> flatpakArguments = ["list", "--app", "--columns=application,version"];
			flatpakArguments.Add(installation.Scope == LinuxPackageScope.System ? "--system" : "--user");
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

	public static async Task<OperationResult> InstallArchRecipeAsync(
		IProcessService processes,
		string recipeDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentException.ThrowIfNullOrWhiteSpace(recipeDirectory);
		cancellationToken.ThrowIfCancellationRequested();

		string? env = processes.FindExecutable("env");
		string? makepkg = processes.FindExecutable("makepkg");
		if (string.IsNullOrWhiteSpace(env) || string.IsNullOrWhiteSpace(makepkg))
			return OperationResult.Fail("PackageToolUnavailable", "makepkg is unavailable, install the base-devel group so Voidstrap can update itself");

		string directory = Path.GetFullPath(recipeDirectory);
		string output = Path.Combine(directory, "packages");
		Directory.CreateDirectory(output);
		OperationResult<ProcessExecution> build = await processes.ExecuteAsync(
			new ProcessCommand(
				env,
				["PKGDEST=" + output, "SRCDEST=" + directory, "BUILDDIR=" + directory, makepkg, "--force", "--clean", "--nodeps", "--noconfirm"],
				StandardInput: string.Empty,
				WorkingDirectory: directory),
			cancellationToken).ConfigureAwait(false);
		if (!build.Succeeded || build.Value is not { ExitCode: 0 })
			return FailureFromExecution("ArchBuildFailed", "The Voidstrap package could not be built", build);

		string? package = Directory.EnumerateFiles(output, "*.pkg.tar*")
			.FirstOrDefault(path => !path.EndsWith(".sig", StringComparison.Ordinal));
		if (package is null)
			return OperationResult.Fail("ArchBuildFailed", "makepkg finished without producing a Voidstrap package");

		List<ProcessCommand> commands = [];
		AddElevatedCommand(processes, commands, "pacman", ["--upgrade", "--noconfirm", package]);
		return await ExecuteCommandsAsync(processes, commands, cancellationToken).ConfigureAwait(false);
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
		foreach ((string elevator, string flag) in new[] { ("sudo", "--non-interactive"), ("doas", "-n"), ("pkexec", "") })
		{
			string? elevation = processes.FindExecutable(elevator);
			if (string.IsNullOrWhiteSpace(elevation))
				continue;
			List<string> elevatedArguments = flag.Length == 0 ? [executable] : [flag, executable];
			elevatedArguments.AddRange(arguments);
			commands.Add(new ProcessCommand(elevation, elevatedArguments));
		}
	}

	private static async Task<OperationResult> ExecuteCommandsAsync(
		IProcessService processes,
		List<ProcessCommand> commands,
		CancellationToken cancellationToken)
	{
		if (commands.Count == 0)
			return OperationResult.Fail("PackageToolUnavailable", "No compatible Linux package tool is available");

		OperationResult<ProcessExecution>? lastResult = null;
		OperationResult<ProcessExecution>? refusedPrompt = null;
		foreach (ProcessCommand command in commands)
		{
			cancellationToken.ThrowIfCancellationRequested();
			bool prompts = Path.GetFileName(command.FileName) == "pkexec";
			if (prompts && refusedPrompt is not null)
				continue;
			lastResult = await processes.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
			if (lastResult.Succeeded && lastResult.Value is { ExitCode: 0 })
				return OperationResult.Success();
			if (prompts && lastResult.Value is { ExitCode: 126 or 127 })
				refusedPrompt = lastResult;
		}
		if (refusedPrompt?.Value is { ExitCode: 126 })
			return OperationResult.Fail(AuthorizationCancelledCode, "The administrator password prompt was cancelled");
		return FailureFromExecution("PackageUpdateFailed", "The Linux package manager could not complete the update", refusedPrompt ?? lastResult);
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
