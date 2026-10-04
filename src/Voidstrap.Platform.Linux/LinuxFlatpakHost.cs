using System.Diagnostics;
using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public static class LinuxFlatpakHost
{
	public const string DefaultApplicationId = LinuxInstallationUpdates.ApplicationId;

	private static readonly string[] SessionVariables = ["DISPLAY", "WAYLAND_DISPLAY", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "DESKTOP_SESSION"];
	private static readonly object SessionEnvironmentGate = new();
	private static Dictionary<string, string> _hostSessionEnvironment = new(StringComparer.Ordinal);
	private static HashSet<string> _hostUnsetSessionVariables = new(StringComparer.Ordinal);


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

			List<string> hostArguments = ["--host", "--directory=/"];
			Dictionary<string, string> sessionEnvironment = GetSessionEnvironment(out IReadOnlyList<string> unsetVariables);
			foreach (KeyValuePair<string, string> variable in sessionEnvironment)
				hostArguments.Add("--env=" + variable.Key + "=" + variable.Value);
			if (unsetVariables.Count > 0)
			{
				hostArguments.Add("env");
				foreach (string variable in unsetVariables)
				{
					hostArguments.Add("-u");
					hostArguments.Add(variable);
				}
				hostArguments.Add("--");
			}
			hostArguments.Add(fileName);
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

	private static Dictionary<string, string> GetSessionEnvironment(out IReadOnlyList<string> unsetVariables)
	{
		Dictionary<string, string> variables = new(StringComparer.Ordinal);
		foreach (string name in SessionVariables)
		{
			string? value = Environment.GetEnvironmentVariable(name);
			if (!string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl))
				variables[name] = value;
		}
		lock (SessionEnvironmentGate)
		{
			foreach (KeyValuePair<string, string> variable in _hostSessionEnvironment)
				variables[variable.Key] = variable.Value;
			unsetVariables = _hostUnsetSessionVariables.ToArray();
			foreach (string variable in unsetVariables)
				variables.Remove(variable);
		}
		return variables;
	}

	public static async Task PrepareSessionEnvironmentAsync(IProcessService processes, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		cancellationToken.ThrowIfCancellationRequested();
		if (!IsSandboxed)
			return;
		string? instance = null;
		try
		{
			if (new FileInfo("/.flatpak-info") is not { Exists: true, Length: <= 65536 })
				return;
			bool inInstance = false;
			foreach (string line in File.ReadLines("/.flatpak-info"))
			{
				string value = line.Trim();
				if (value.StartsWith('['))
					inInstance = value == "[Instance]";
				else if (inInstance && value.StartsWith("instance-id=", StringComparison.Ordinal))
				{
					instance = value["instance-id=".Length..];
					break;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}
		if (instance is null || instance.Length is 0 or > 20 || !instance.All(char.IsAsciiDigit))
			return;
		string? spawn = processes.FindExecutable("flatpak-spawn");
		if (string.IsNullOrWhiteSpace(spawn))
			return;
		const string probe = """
			runtime="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
			file="$runtime/.flatpak/$1-private/run-environ"
			[ -r "$file" ] || exit 0
			[ "$(wc -c < "$file")" -le 262144 ] || exit 0
			LC_ALL=C sed -zn '/^\(DISPLAY\|WAYLAND_DISPLAY\|XDG_RUNTIME_DIR\|XAUTHORITY\|DBUS_SESSION_BUS_ADDRESS\|XDG_SESSION_TYPE\|XDG_CURRENT_DESKTOP\|XDG_SESSION_DESKTOP\|DESKTOP_SESSION\)=/p' "$file" || exit 0
			printf '\000VOIDSTRAP_SESSION_READY\000'
			""";
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(3));
		OperationResult<ProcessExecution> result;
		try
		{
			result = await processes.ExecuteAsync(new ProcessCommand(spawn, ["--host", "--directory=/", "sh", "-c", probe, "voidstrap", instance]), timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return;
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (!result.Succeeded || result.Value is not { ExitCode: 0 } || result.Value.StandardOutput.Length > 65536)
			return;
		if (!result.Value.StandardOutput.EndsWith("\0VOIDSTRAP_SESSION_READY\0", StringComparison.Ordinal))
			return;
		Dictionary<string, string> variables = new(StringComparer.Ordinal);
		HashSet<string> unsetVariables = new(SessionVariables, StringComparer.Ordinal) { "XAUTHORITY" };
		foreach (string entry in result.Value.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			int equals = entry.IndexOf('=');
			if (equals <= 0)
				continue;
			string name = entry[..equals], value = entry[(equals + 1)..];
			if ((!SessionVariables.Contains(name, StringComparer.Ordinal) && name is not ("XDG_RUNTIME_DIR" or "XAUTHORITY" or "DBUS_SESSION_BUS_ADDRESS"))
				|| string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
				continue;
			if (name is "XDG_RUNTIME_DIR" or "XAUTHORITY" && !Path.IsPathRooted(value))
				continue;
			variables[name] = value;
			unsetVariables.Remove(name);
		}
		lock (SessionEnvironmentGate)
		{
			_hostSessionEnvironment = variables;
			_hostUnsetSessionVariables = unsetVariables;
		}
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
		int hostOptions = 0;
		if (hostSpawn)
			while (hostOptions < command.Arguments.Count && command.Arguments[hostOptions].StartsWith("--", StringComparison.Ordinal))
				hostOptions++;
		int unsetOptionsEnd = hostOptions;
		if (hostSpawn && hostOptions < command.Arguments.Count && command.Arguments[hostOptions] == "env")
		{
			unsetOptionsEnd++;
			while (unsetOptionsEnd < command.Arguments.Count && command.Arguments[unsetOptionsEnd] != "--")
				unsetOptionsEnd++;
		}
		for (int index = 0; index < command.Arguments.Count; index++)
		{
			string argument = command.Arguments[index];
			if (hostSpawn && index > hostOptions && index < unsetOptionsEnd && argument == "-u"
				&& index + 1 < unsetOptionsEnd && environment?.ContainsKey(command.Arguments[index + 1]) == true)
			{
				index++;
				continue;
			}
			if (hostSpawn && index < hostOptions && environment is not null && argument.StartsWith("--env=", StringComparison.Ordinal)
				&& environment.ContainsKey(argument["--env=".Length..].Split('=', 2)[0]))
				continue;
			startInfo.ArgumentList.Add(argument);
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
