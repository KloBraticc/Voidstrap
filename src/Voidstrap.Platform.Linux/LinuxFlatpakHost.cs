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
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentNullException.ThrowIfNull(arguments);

		if (IsSandboxed)
		{
			string? spawn = processes.FindExecutable("flatpak-spawn");
			if (string.IsNullOrWhiteSpace(spawn))
			{
				command = new ProcessCommand(string.Empty, []);
				return false;
			}

			List<string> hostArguments = ["--host", "flatpak"];
			hostArguments.AddRange(arguments);
			command = new ProcessCommand(spawn, hostArguments, CaptureOutput: captureOutput);
			return true;
		}

		string? flatpak = processes.FindExecutable("flatpak");
		if (string.IsNullOrWhiteSpace(flatpak))
		{
			command = new ProcessCommand(string.Empty, []);
			return false;
		}

		command = new ProcessCommand(flatpak, arguments, CaptureOutput: captureOutput);
		return true;
	}

	public static Process? Start(IReadOnlyList<string> arguments)
	{
		SystemProcessService processes = new();
		if (!TryCreateCommand(processes, arguments, out ProcessCommand command, false))
			return null;

		ProcessStartInfo startInfo = new(command.FileName)
		{
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (string argument in command.Arguments)
			startInfo.ArgumentList.Add(argument);
		return Process.Start(startInfo);
	}
}
