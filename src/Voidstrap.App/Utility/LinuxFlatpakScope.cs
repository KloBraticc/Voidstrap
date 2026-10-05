using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class LinuxFlatpakScope
{
	private const string LogIdent = "LinuxFlatpakScope";

	private const string ScopeScript = """
		bw=$(flatpak ps --columns=instance,pid 2>/dev/null | awk -v id="$1" '$1 == id { print $2; exit }')
		[ -n "$bw" ] || exit 3
		cg=$(sed -n 's/^0:://p' "/proc/$bw/cgroup" 2>/dev/null)
		[ -n "$cg" ] && [ -r "/sys/fs/cgroup$cg/cgroup.procs" ] || exit 4
		printf 'scope %s %s\n' "$bw" "$cg"
		while read -r p; do
			st=$(cat "/proc/$p/stat" 2>/dev/null) || continue
			rest=${st##*) }
			set -- $rest
			comm=$(cat "/proc/$p/comm" 2>/dev/null)
			if tr '\0' '\n' < "/proc/$p/cmdline" 2>/dev/null | grep -qxE '(.*/)?xdg-dbus-proxy'; then proxy=1; else proxy=0; fi
			printf 'proc %s %s %s %s\n' "$p" "$2" "$proxy" "$comm"
		done < "/sys/fs/cgroup$cg/cgroup.procs"
		""";

	private static readonly Dictionary<string, string> KnownLaunchers = new(StringComparer.Ordinal)
	{
		["gnome-software"] = "gnome-org.gnome.Software",
		["plasma-discover"] = "org.kde.discover",
		["mintinstall"] = "mintinstall"
	};

	private sealed record ScopeProcess(int Pid, int ParentPid, bool Proxy, string Name);

	public static async Task ReleaseLauncherAsync(CancellationToken token)
	{
		if (!LinuxFlatpakHost.IsSandboxed)
			return;

		try
		{
			await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
			string applicationId = Environment.GetEnvironmentVariable("FLATPAK_ID") ?? string.Empty;
			string? instanceId = ReadInstanceId();
			if (applicationId.Length == 0 || instanceId is null)
				return;
			await ReleaseForeignProcessesAsync(applicationId, instanceId, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The Flatpak scope could not be checked: " + ex.Message);
		}
	}

	internal static async Task<int> ReleaseForeignProcessesAsync(string applicationId, string instanceId, CancellationToken token)
	{
		ProcessExecution? listing = await RunHostAsync("sh", ["-c", ScopeScript, "sh", instanceId], token).ConfigureAwait(false);
		if (listing is null || listing.ExitCode != 0)
			return 0;

		int sandboxPid = 0;
		string scopePath = string.Empty;
		Dictionary<int, ScopeProcess> processes = [];
		foreach (string line in listing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = line.TrimEnd('\r').Split(' ', 5);
			if (parts.Length >= 3 && parts[0] == "scope")
			{
				int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sandboxPid);
				scopePath = parts[2];
			}
			else if (parts.Length == 5 && parts[0] == "proc"
				&& int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
				&& int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parent))
			{
				processes[pid] = new ScopeProcess(pid, parent, parts[3] == "1", parts[4].Trim());
			}
		}

		string scopeName = Path.GetFileName(scopePath);
		if (sandboxPid <= 0 || !processes.ContainsKey(sandboxPid) || !scopeName.StartsWith("app-flatpak-" + applicationId + "-", StringComparison.Ordinal) || !scopeName.EndsWith(".scope", StringComparison.Ordinal))
			return 0;

		Dictionary<int, List<int>> foreign = [];
		foreach (ScopeProcess process in processes.Values)
		{
			if (BelongsToSandbox(process, sandboxPid, processes))
				continue;
			int root = TopForeignAncestor(process, sandboxPid, processes);
			if (!foreign.TryGetValue(root, out List<int>? group))
				foreign[root] = group = [];
			group.Add(process.Pid);
		}

		int moved = 0;
		foreach ((int root, List<int> group) in foreign)
		{
			string name = processes[root].Name;
			string unit = "app-" + LauncherUnitId(name) + "-" + root.ToString(CultureInfo.InvariantCulture) + ".scope";
			List<string> arguments =
			[
				"--user", "call", "org.freedesktop.systemd1", "/org/freedesktop/systemd1", "org.freedesktop.systemd1.Manager",
				"StartTransientUnit", "ssa(sv)a(sa(sv))", unit, "fail", "2",
				"PIDs", "au", group.Count.ToString(CultureInfo.InvariantCulture)
			];
			arguments.AddRange(group.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
			arguments.AddRange(["Description", "s", name.Length > 0 ? name : unit, "0"]);
			ProcessExecution? result = await RunHostAsync("busctl", arguments, token).ConfigureAwait(false);
			if (result is not null && result.ExitCode == 0)
			{
				moved += group.Count;
				App.Logger.WriteLine(LogIdent, $"The app that opened Voidstrap (pid {root}) was placed in the Voidstrap scope by Flatpak, moved {group.Count} of its processes to {unit} so system monitors stop counting it as Voidstrap");
			}
			else
			{
				App.Logger.WriteLine(LogIdent, $"The app that opened Voidstrap (pid {root}) shares the Voidstrap scope and could not be moved out: " + (result?.StandardError.Trim() ?? "busctl is unavailable"));
			}
		}
		return moved;
	}

	private static bool BelongsToSandbox(ScopeProcess process, int sandboxPid, Dictionary<int, ScopeProcess> processes)
	{
		ScopeProcess? current = process;
		for (int depth = 0; current is not null && depth < 64; depth++)
		{
			if (current.Pid == sandboxPid || current.Proxy)
				return true;
			processes.TryGetValue(current.ParentPid, out current);
		}
		return false;
	}

	private static int TopForeignAncestor(ScopeProcess process, int sandboxPid, Dictionary<int, ScopeProcess> processes)
	{
		ScopeProcess current = process;
		for (int depth = 0; depth < 64; depth++)
		{
			if (!processes.TryGetValue(current.ParentPid, out ScopeProcess? parent) || parent.Pid == sandboxPid || parent.Proxy)
				break;
			current = parent;
		}
		return current.Pid;
	}

	private static string LauncherUnitId(string name)
	{
		if (KnownLaunchers.TryGetValue(name, out string? known))
			return known;
		StringBuilder builder = new();
		foreach (char character in name)
			builder.Append(char.IsAsciiLetterOrDigit(character) || character == '.' ? character : '_');
		return builder.Length > 0 ? builder.ToString() : "launcher";
	}

	private static string? ReadInstanceId()
	{
		try
		{
			bool instanceSection = false;
			foreach (string raw in File.ReadLines("/.flatpak-info"))
			{
				string line = raw.Trim();
				if (line.StartsWith('['))
				{
					instanceSection = line == "[Instance]";
					continue;
				}
				if (instanceSection && line.StartsWith("instance-id=", StringComparison.Ordinal))
				{
					string id = line["instance-id=".Length..];
					return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		return null;
	}

	private static async Task<ProcessExecution?> RunHostAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
	{
		Voidstrap.Core.SystemProcessService processes = new();
		if (!LinuxFlatpakHost.TryCreateHostCommand(processes, fileName, arguments, out ProcessCommand command))
			return null;
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, token).ConfigureAwait(false);
		token.ThrowIfCancellationRequested();
		return result.Succeeded ? result.Value : null;
	}
}
