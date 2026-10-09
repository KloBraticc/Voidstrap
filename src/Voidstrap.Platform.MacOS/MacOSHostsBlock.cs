using Voidstrap.Core;

namespace Voidstrap.Platform.MacOS;

public static class MacOSHostsBlock
{
	public const string HostsPath = "/etc/hosts";

	public static async Task<OperationResult> WriteAsync(string marker, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(marker);
		ArgumentNullException.ThrowIfNull(lines);
		if (!File.Exists("/usr/bin/osascript"))
			return OperationResult.Fail("PrivilegeToolMissing", "The macOS administrator prompt is unavailable, so the hosts file cannot be changed");

		List<string> content = File.Exists(HostsPath)
			? File.ReadAllLines(HostsPath).Where(line => !line.Contains(marker, StringComparison.Ordinal)).ToList()
			: [];
		if (lines.Count > 0)
		{
			if (content.Count > 0 && !string.IsNullOrWhiteSpace(content[^1]))
				content.Add(string.Empty);
			content.AddRange(lines);
		}

		string staged = Path.Combine(Path.GetTempPath(), "voidstrap-hosts-" + Guid.NewGuid().ToString("N"));
		try
		{
			await File.WriteAllLinesAsync(staged, content, cancellationToken).ConfigureAwait(false);
			string shell = "cat " + ShellQuote(staged) + " > " + HostsPath + " && /usr/bin/dscacheutil -flushcache; /usr/bin/killall -HUP mDNSResponder 2>/dev/null; true";
			string script = "do shell script " + AppleScriptQuote(shell) + " with prompt \"Voidstrap needs to update the hosts file.\" with administrator privileges";
			OperationResult<ProcessExecution> result = await new SystemProcessService()
				.ExecuteAsync(new ProcessCommand("/usr/bin/osascript", ["-e", script]), cancellationToken)
				.ConfigureAwait(false);
			if (!result.Succeeded || result.Value is null)
				return OperationResult.Fail("HostsWriteFailed", result.Failure?.Message ?? "The hosts file could not be changed");
			if (result.Value.ExitCode != 0)
			{
				string detail = string.IsNullOrWhiteSpace(result.Value.StandardError) ? result.Value.StandardOutput : result.Value.StandardError;
				return detail.Contains("-128", StringComparison.Ordinal)
					? OperationResult.Fail("HostsWriteDenied", "Changing the hosts file was not authorized")
					: OperationResult.Fail("HostsWriteFailed", "The hosts file could not be changed: " + detail.Trim());
			}
			return OperationResult.Success();
		}
		finally
		{
			try
			{
				File.Delete(staged);
			}
			catch (IOException)
			{
			}
		}
	}

	private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

	private static string AppleScriptQuote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
