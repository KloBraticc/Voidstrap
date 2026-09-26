using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public readonly record struct LinuxInputAccessResult(bool Success, bool RestartRequired, string Message);

public static class LinuxInputAccess
{
	public const string RulePath = "/etc/udev/rules.d/70-voidstrap-snaptap.rules";

	private const string Script = "set -e\n"
		+ "printf '%s\\n' "
		+ "'KERNEL==\"uinput\", SUBSYSTEM==\"misc\", TAG+=\"uaccess\", OPTIONS+=\"static_node=uinput\"' "
		+ "'SUBSYSTEM==\"input\", KERNEL==\"event*\", ENV{ID_INPUT_KEYBOARD}==\"1\", TAG+=\"uaccess\"' "
		+ "> " + RulePath + "\n"
		+ "modprobe uinput 2>/dev/null || true\n"
		+ "udevadm control --reload-rules\n"
		+ "udevadm trigger --action=change --subsystem-match=misc --sysname-match=uinput\n"
		+ "udevadm trigger --action=change --subsystem-match=input\n"
		+ "udevadm settle --timeout=5 || true\n";

	public static async Task<LinuxInputAccessResult> GrantAsync(CancellationToken cancellationToken = default)
	{
		SystemProcessService processes = new();
		bool sandboxed = LinuxFlatpakHost.IsSandboxed;
		if (sandboxed)
		{
			if (!LinuxFlatpakHost.TryCreateCommand(processes, ["override", "--user", "--device=all", LinuxFlatpakHost.CurrentApplicationId], out ProcessCommand overrideCommand))
				return new LinuxInputAccessResult(false, false, "Voidstrap could not reach Flatpak to allow keyboard access");
			OperationResult<ProcessExecution> overrideResult = await processes.ExecuteAsync(overrideCommand, cancellationToken).ConfigureAwait(false);
			if (!overrideResult.Succeeded || overrideResult.Value is null || overrideResult.Value.ExitCode != 0)
				return new LinuxInputAccessResult(false, false, "Flatpak did not allow keyboard access for Voidstrap");
		}

		ProcessCommand? privileged = CreatePrivilegedCommand(processes, sandboxed);
		if (privileged is null)
			return new LinuxInputAccessResult(false, false, "The pkexec command is unavailable, so keyboard access cannot be granted");

		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(privileged, cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null)
			return new LinuxInputAccessResult(false, false, "Keyboard access could not be granted");
		if (result.Value.ExitCode is 126 or 127)
			return new LinuxInputAccessResult(false, false, LinuxSteamOS.WithPrivilegeHint("Keyboard access was not authorized"));
		if (result.Value.ExitCode != 0)
		{
			string detail = string.IsNullOrWhiteSpace(result.Value.StandardError) ? result.Value.StandardOutput : result.Value.StandardError;
			return new LinuxInputAccessResult(false, false, LinuxSteamOS.WithPrivilegeHint("Keyboard access could not be granted: " + detail.Trim()));
		}

		if (sandboxed)
			return new LinuxInputAccessResult(true, true, "Keyboard access is allowed. Restart Voidstrap so the Flatpak sandbox can see your keyboard");

		for (int attempt = 0; attempt < 10; attempt++)
		{
			if (LinuxKeyboardInterceptor.CheckAccess() == LinuxInputAccessState.Ready)
				return new LinuxInputAccessResult(true, false, "Keyboard access is ready");
			await Task.Delay(300, cancellationToken).ConfigureAwait(false);
		}
		return new LinuxInputAccessResult(true, true, "Keyboard access was granted. Sign out and back in if Snap Tap still cannot see your keyboard");
	}

	private static ProcessCommand? CreatePrivilegedCommand(SystemProcessService processes, bool sandboxed)
	{
		List<string> arguments = [];
		string fileName;
		if (sandboxed)
		{
			string? spawn = processes.FindExecutable("flatpak-spawn");
			if (string.IsNullOrWhiteSpace(spawn))
				return null;
			fileName = spawn;
			arguments.Add("--host");
			arguments.Add("pkexec");
		}
		else
		{
			string? pkexec = processes.FindExecutable("pkexec");
			if (string.IsNullOrWhiteSpace(pkexec))
				return null;
			fileName = pkexec;
		}

		arguments.Add("/bin/sh");
		arguments.Add("-c");
		arguments.Add(Script);
		arguments.Add("voidstrap");
		return new ProcessCommand(fileName, arguments);
	}
}
