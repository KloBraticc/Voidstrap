namespace Voidstrap.Platform.Linux;

public enum SoberGraphicsOptimizationMode
{
	Quality,
	Balanced,
	Performance
}

public enum SoberTouchMode
{
	Off,
	On,
	FakeOff
}

public sealed record SoberNativeConfigurationOptions(
	bool? AllowGamepadPermission = null,
	bool? CloseOnLeave = null,
	bool? DiscordRpcEnabled = null,
	bool? DiscordRpcShowJoinButton = null,
	bool? EnableGameMode = null,
	bool? EnableHiDpi = null,
	bool? EnableMobileHomeScreen = null,
	SoberGraphicsOptimizationMode? GraphicsOptimizationMode = null,
	bool? ServerLocationIndicatorEnabled = null,
	SoberTouchMode? TouchMode = null,
	bool? UseConsoleExperience = null,
	bool? UseLibsecret = null,
	bool? UseOpenGl = null);

public static class SoberNativeSettings
{
	private const string ServerLocationIndicatorKey = "server_location_indicator_enabled";

	private static readonly System.Text.Json.JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = true,
		CommentHandling = System.Text.Json.JsonCommentHandling.Skip
	};

	public static bool IsServerLocationIndicatorEnabled()
	{
		return ReadBoolean(ServerLocationIndicatorKey);
	}

	private static bool ReadBoolean(string name)
	{
		string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string path = Path.Combine(home, ".var", "app", "org.vinegarhq.Sober", "config", "sober", "config.json");
		try
		{
			if (!File.Exists(path))
				return false;
			using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), DocumentOptions);
			return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
				&& document.RootElement.TryGetProperty(name, out System.Text.Json.JsonElement value)
				&& value.ValueKind == System.Text.Json.JsonValueKind.True;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return false;
		}
	}
}

public sealed record LinuxModSource(string RelativePath, string SourcePath);

public sealed record LinuxPlayerPreparationOptions(
	bool UseFastFlagManager = true,
	SoberNativeConfigurationOptions? NativeConfiguration = null,
	bool ApplyModifications = true,
	IReadOnlyList<LinuxModSource>? AdditionalModSources = null,
	Func<string, bool>? IgnoreModFile = null,
	IReadOnlyDictionary<string, IReadOnlyList<string>>? RecoverableAssetHashes = null);

public interface ISoberProcessProbe
{
	Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);
}

public sealed class LinuxSoberProcessProbe : ISoberProcessProbe
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";

	private readonly IProcessService _processes;

	public LinuxSoberProcessProbe(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	private const string SoberProcessName = "sober";

	private const long HostProbeInterval = 2000;

	private static readonly object HostProbeSync = new();

	private static long _hostProbedAt = long.MinValue;

	private static bool _hostProbeRunning;

	public static bool IsRunningNow()
	{
		if (HasLiveSoberProcess())
			return true;
		return LinuxFlatpakHost.IsSandboxed && IsRunningOnHost();
	}

	private static bool IsRunningOnHost()
	{
		lock (HostProbeSync)
		{
			if (_hostProbedAt != long.MinValue && Environment.TickCount64 - _hostProbedAt < HostProbeInterval)
				return _hostProbeRunning;

			bool running = false;
			try
			{
				Voidstrap.Core.SystemProcessService processes = new();
				if (LinuxFlatpakHost.TryCreateHostCommand(processes, "sh", ["-c", LiveSoberProbe], out ProcessCommand command))
				{
					using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
					OperationResult<ProcessExecution> result = Task.Run(() => processes.ExecuteAsync(command, timeout.Token)).GetAwaiter().GetResult();
					running = result.Succeeded && result.Value is { ExitCode: 0 } && string.Equals(result.Value.StandardOutput.Trim(), "running", StringComparison.Ordinal);
				}
			}
			catch (Exception)
			{
				running = false;
			}

			_hostProbeRunning = running;
			_hostProbedAt = Environment.TickCount64;
			return running;
		}
	}

	private const string LiveSoberProbe = """
		for file in /proc/[0-9]*/comm; do
			IFS= read -r name < "$file" 2>/dev/null || continue
			if [ "$name" != sober ]; then
				matched=
				while IFS= read -r group; do
					case "$group" in *org.vinegarhq.Sober*) matched=1; break ;; esac
				done < "${file%/comm}/cgroup" 2>/dev/null
				[ "$matched" = 1 ] || continue
				[ "$(readlink "${file%/comm}/exe" 2>/dev/null)" = /app/bin/sober ] || continue
			fi
			IFS= read -r status < "${file%/comm}/stat" 2>/dev/null || continue
			case "${status##*) }" in Z*|X*) continue ;; esac
			printf 'running\n'
			exit 0
		done
		""";

	private static bool HasLiveSoberProcess()
	{
		foreach (int processId in GetSandboxProcessIds())
		{
			try
			{
				string directory = Path.Combine("/proc", processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
				if (!string.Equals(File.ReadAllText(Path.Combine(directory, "comm")).Trim(), SoberProcessName, StringComparison.Ordinal)
					&& !(string.Equals(new FileInfo(Path.Combine(directory, "exe")).LinkTarget, "/app/bin/sober", StringComparison.Ordinal)
						&& File.ReadAllText(Path.Combine(directory, "cgroup")).Contains(SoberApplicationId, StringComparison.OrdinalIgnoreCase)))
					continue;
				string status = File.ReadAllText(Path.Combine(directory, "stat"));
				int state = status.LastIndexOf(')') + 2;
				if (state >= 2 && state < status.Length && status[state] is not ('Z' or 'X'))
					return true;
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
		return false;
	}

	public static IReadOnlyList<int> GetSandboxProcessIds()
	{
		List<int> processIds = [];
		string[] directories;
		try
		{
			directories = Directory.GetDirectories("/proc");
		}
		catch (Exception)
		{
			return processIds;
		}

		foreach (string directory in directories)
		{
			string name = Path.GetFileName(directory);
			if (name.Length == 0
				|| !char.IsAsciiDigit(name[0])
				|| !int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int processId))
				continue;

			try
			{
				if (File.ReadAllText(Path.Combine(directory, "cgroup")).Contains(SoberApplicationId, StringComparison.OrdinalIgnoreCase))
				{
					processIds.Add(processId);
					continue;
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			try
			{
				if (string.Equals(File.ReadAllText(Path.Combine(directory, "comm")).Trim(), SoberProcessName, StringComparison.OrdinalIgnoreCase))
					processIds.Add(processId);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		return processIds;
	}

	public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (HasLiveSoberProcess())
			return true;

		if (!LinuxFlatpakHost.TryCreateHostCommand(_processes, "sh", ["-c", LiveSoberProbe], out ProcessCommand command))
			return false;

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
			return false;

		return string.Equals(result.Value.StandardOutput.Trim(), "running", StringComparison.Ordinal);
	}
}
