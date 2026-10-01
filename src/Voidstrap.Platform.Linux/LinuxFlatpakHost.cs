using System.Diagnostics;
using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public static class LinuxFlatpakHost
{
	public const string DefaultApplicationId = LinuxInstallationUpdates.ApplicationId;

	public static bool IsSandboxed => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATPAK_ID"))
		|| File.Exists("/.flatpak-info");

	public static string CurrentApplicationId
	{
		get
		{
			string value = Environment.GetEnvironmentVariable("FLATPAK_ID") ?? string.Empty;
			value = value.Trim();
			return value.Length is > 0 and <= 255 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
				? value
				: DefaultApplicationId;
		}
	}

	public static bool TryCreateCommand(
		IProcessService processes,
		IReadOnlyList<string> arguments,
		out ProcessCommand command,
		bool captureOutput = true)
	{
		return TryCreateHostCommand(processes, "flatpak", arguments, out command, captureOutput);
	}

	public static bool TryCreateHostCommand(
		IProcessService processes,
		string fileName,
		IReadOnlyList<string> arguments,
		out ProcessCommand command,
		bool captureOutput = true)
	{
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		ArgumentNullException.ThrowIfNull(arguments);

		if (IsSandboxed)
		{
			string? spawn = processes.FindExecutable("flatpak-spawn");
			if (string.IsNullOrWhiteSpace(spawn))
			{
				command = new ProcessCommand(string.Empty, []);
				return false;
			}

			List<string> hostArguments = ["--host", "--directory=/", fileName];
			hostArguments.AddRange(arguments);
			command = new ProcessCommand(spawn, hostArguments, CaptureOutput: captureOutput);
			return true;
		}

		string? executable = processes.FindExecutable(fileName);
		if (string.IsNullOrWhiteSpace(executable))
		{
			command = new ProcessCommand(string.Empty, []);
			return false;
		}

		command = new ProcessCommand(executable, arguments, CaptureOutput: captureOutput);
		return true;
	}

	public static async Task<string?> FindExecutableAsync(IProcessService processes, string fileName, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		cancellationToken.ThrowIfCancellationRequested();
		if (!IsSandboxed)
			return processes.FindExecutable(fileName);
		if (!TryCreateHostCommand(processes, "sh", ["-c", "command -v \"$1\"", "voidstrap", fileName], out ProcessCommand command))
			return null;

		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (result.Failure?.Code == "OperationCanceled")
			throw new OperationCanceledException(result.Failure.Message, cancellationToken);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
			return null;
		string path = result.Value.StandardOutput.Trim();
		return Path.IsPathRooted(path) && !path.Any(char.IsControl) ? path : null;
	}

	internal static string GetHostXdgDirectory(string variable, params string[] fallbackSegments)
	{
		string? configured = Environment.GetEnvironmentVariable(IsSandboxed ? "HOST_" + variable : variable);
		if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
			return Path.GetFullPath(configured);
		string path = LinuxPaths.GetHomeDirectory();
		foreach (string segment in fallbackSegments)
			path = Path.Combine(path, segment);
		return path;
	}

	public static Process? Start(IReadOnlyList<string> arguments) => Start(arguments, null);

	public static Process? Start(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
	{
		SystemProcessService processes = new();
		if (!TryCreateCommand(processes, arguments, out ProcessCommand command, false))
			return null;

		ProcessStartInfo startInfo = new(command.FileName)
		{
			UseShellExecute = false,
			CreateNoWindow = true
		};
		bool hostSpawn = IsSandboxed && command.Arguments.Count > 0 && command.Arguments[0] == "--host";
		for (int index = 0; index < command.Arguments.Count; index++)
		{
			startInfo.ArgumentList.Add(command.Arguments[index]);
			if (hostSpawn && index == 0 && environment is not null)
			{
				foreach (KeyValuePair<string, string> variable in environment)
					startInfo.ArgumentList.Add("--env=" + variable.Key + "=" + variable.Value);
			}
		}
		if (!hostSpawn && environment is not null)
		{
			foreach (KeyValuePair<string, string> variable in environment)
				startInfo.Environment[variable.Key] = variable.Value;
		}
		return Process.Start(startInfo);
	}
}
