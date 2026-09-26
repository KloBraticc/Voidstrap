using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public static class LinuxHostsBlock
{
	public const string HostsPath = "/etc/hosts";

	private const string Script = "set -e\n"
		+ "marker=\"$1\"\n"
		+ "shift\n"
		+ "staged=$(mktemp)\n"
		+ "trap 'rm -f \"$staged\"' EXIT\n"
		+ "grep -vF \"$marker\" " + HostsPath + " > \"$staged\" || true\n"
		+ "if [ \"$#\" -gt 0 ]; then\n"
		+ "  if [ -s \"$staged\" ] && [ -n \"$(tail -c 1 \"$staged\")\" ]; then printf '\\n' >> \"$staged\"; fi\n"
		+ "  for line in \"$@\"; do printf '%s\\n' \"$line\" >> \"$staged\"; done\n"
		+ "fi\n"
		+ "cat \"$staged\" > " + HostsPath + "\n"
		+ "command -v resolvectl >/dev/null 2>&1 && resolvectl flush-caches >/dev/null 2>&1 || true\n";

	public static async Task<OperationResult> WriteAsync(string marker, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(marker);
		ArgumentNullException.ThrowIfNull(lines);

		SystemProcessService processes = new();
		bool sandboxed = LinuxFlatpakHost.IsSandboxed;
		string? fileName = sandboxed ? processes.FindExecutable("flatpak-spawn") : processes.FindExecutable("pkexec");
		if (string.IsNullOrWhiteSpace(fileName))
			return OperationResult.Fail("PrivilegeToolMissing", "The pkexec command is unavailable, so the hosts file cannot be changed");

		List<string> arguments = [];
		if (sandboxed)
		{
			arguments.Add("--host");
			arguments.Add("pkexec");
		}
		arguments.Add("/bin/sh");
		arguments.Add("-c");
		arguments.Add(Script);
		arguments.Add("voidstrap");
		arguments.Add(marker);
		arguments.AddRange(lines);

		OperationResult<ProcessExecution> result = await processes
			.ExecuteAsync(new ProcessCommand(fileName, arguments), cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null)
			return OperationResult.Fail("HostsWriteFailed", result.Failure?.Message ?? "The hosts file could not be changed");
		if (result.Value.ExitCode is 126 or 127)
			return OperationResult.Fail("HostsWriteDenied", LinuxSteamOS.WithPrivilegeHint("Changing the hosts file was not authorized"));
		if (result.Value.ExitCode != 0)
		{
			string detail = string.IsNullOrWhiteSpace(result.Value.StandardError) ? result.Value.StandardOutput : result.Value.StandardError;
			return OperationResult.Fail("HostsWriteFailed", LinuxSteamOS.WithPrivilegeHint("The hosts file could not be changed: " + detail.Trim()));
		}
		return OperationResult.Success();
	}
}
