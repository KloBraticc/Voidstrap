using System.Globalization;
using System.Text.Json;
using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public sealed record LinuxAudioStream(int NodeId, float Volume);

public static class LinuxAudioSessions
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";
	private const string SoberApplicationName = "Sober";
	private const string SoberBinary = "sober";

	private static readonly SystemProcessService Processes = new();

	public static bool IsAvailable
	{
		get
		{
			if (LinuxFlatpakHost.IsSandboxed)
				return !string.IsNullOrWhiteSpace(Processes.FindExecutable("flatpak-spawn"));
			return !string.IsNullOrWhiteSpace(Processes.FindExecutable("pw-dump"))
				&& !string.IsNullOrWhiteSpace(Processes.FindExecutable("wpctl"));
		}
	}

	public static FileStream? TryAcquireOwnership()
	{
		string directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath();
		try
		{
			return new FileStream(Path.Combine(directory, "voidstrap-audio.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	public static async Task<IReadOnlyList<LinuxAudioStream>?> FindSoberStreamsAsync(CancellationToken cancellationToken = default)
	{
		IReadOnlyList<int> soberProcessIds = LinuxSoberProcessProbe.GetSandboxProcessIds();
		if (soberProcessIds.Count == 0 && !LinuxFlatpakHost.IsSandboxed)
			return [];
		OperationResult<ProcessExecution> result = await RunAsync("pw-dump", [], cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
			return null;
		try
		{
			return ParseSoberStreams(result.Value.StandardOutput, soberProcessIds);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static async Task<bool> SetVolumeAsync(int nodeId, float linearVolume, CancellationToken cancellationToken = default)
	{
		double cubic = Math.Cbrt(Math.Clamp(linearVolume, 0f, 1.5f));
		OperationResult<ProcessExecution> result = await RunAsync(
			"wpctl",
			["set-volume", nodeId.ToString(CultureInfo.InvariantCulture), cubic.ToString("0.0000", CultureInfo.InvariantCulture)],
			cancellationToken).ConfigureAwait(false);
		return result.Succeeded && result.Value is not null && result.Value.ExitCode == 0;
	}

	public static IReadOnlyList<LinuxAudioStream> ParseSoberStreams(string dump, IReadOnlyCollection<int> soberProcessIds)
	{
		List<LinuxAudioStream> streams = [];
		if (string.IsNullOrWhiteSpace(dump))
			return streams;

		using JsonDocument document = JsonDocument.Parse(dump);
		if (document.RootElement.ValueKind != JsonValueKind.Array)
			return streams;

		Dictionary<int, JsonElement> clients = [];
		foreach (JsonElement item in document.RootElement.EnumerateArray())
		{
			if (TryGetString(item, "type") == "PipeWire:Interface:Client"
				&& item.TryGetProperty("id", out JsonElement id)
				&& id.TryGetInt32(out int clientId)
				&& TryGetProps(item, out JsonElement props))
				clients[clientId] = props;
		}

		foreach (JsonElement item in document.RootElement.EnumerateArray())
		{
			if (TryGetString(item, "type") != "PipeWire:Interface:Node"
				|| !item.TryGetProperty("id", out JsonElement id)
				|| !id.TryGetInt32(out int nodeId)
				|| !TryGetProps(item, out JsonElement props)
				|| TryGetString(props, "media.class") != "Stream/Output/Audio")
				continue;

			JsonElement? client = null;
			if (TryGetInt(props, "client.id") is int clientId && clients.TryGetValue(clientId, out JsonElement clientProps))
				client = clientProps;

			if (!IsSober(props, soberProcessIds) && (client is not JsonElement found || !IsSober(found, soberProcessIds)))
				continue;

			streams.Add(new LinuxAudioStream(nodeId, ReadLinearVolume(item)));
		}

		return streams;
	}

	private static bool IsSober(JsonElement props, IReadOnlyCollection<int> soberProcessIds)
	{
		if (string.Equals(TryGetString(props, "pipewire.access.portal.app_id"), SoberApplicationId, StringComparison.Ordinal)
			|| string.Equals(TryGetString(props, "application.name"), SoberApplicationName, StringComparison.Ordinal)
			|| string.Equals(TryGetString(props, "application.process.binary"), SoberBinary, StringComparison.Ordinal))
			return true;

		return TryGetInt(props, "pipewire.sec.pid") is int processId && soberProcessIds.Contains(processId);
	}

	private static float ReadLinearVolume(JsonElement node)
	{
		if (!node.TryGetProperty("info", out JsonElement info)
			|| !info.TryGetProperty("params", out JsonElement parameters)
			|| parameters.ValueKind != JsonValueKind.Object
			|| !parameters.TryGetProperty("Props", out JsonElement entries)
			|| entries.ValueKind != JsonValueKind.Array)
			return 1f;

		foreach (JsonElement entry in entries.EnumerateArray())
		{
			if (entry.ValueKind != JsonValueKind.Object
				|| !entry.TryGetProperty("channelVolumes", out JsonElement channels)
				|| channels.ValueKind != JsonValueKind.Array)
				continue;

			float highest = 0f;
			int count = 0;
			foreach (JsonElement channel in channels.EnumerateArray())
			{
				if (channel.TryGetDouble(out double value))
				{
					highest = Math.Max(highest, (float)value);
					count++;
				}
			}
			if (count > 0)
				return highest;
		}

		return 1f;
	}

	private static bool TryGetProps(JsonElement item, out JsonElement props)
	{
		props = default;
		return item.TryGetProperty("info", out JsonElement info)
			&& info.ValueKind == JsonValueKind.Object
			&& info.TryGetProperty("props", out props)
			&& props.ValueKind == JsonValueKind.Object;
	}

	private static string? TryGetString(JsonElement element, string name)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(name, out JsonElement value)
			&& value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;
	}

	private static int? TryGetInt(JsonElement element, string name)
	{
		if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
			return null;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
			return number;
		if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
			return number;
		return null;
	}

	private static Task<OperationResult<ProcessExecution>> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		string? fileName;
		List<string> commandArguments = [];
		if (LinuxFlatpakHost.IsSandboxed)
		{
			fileName = Processes.FindExecutable("flatpak-spawn");
			commandArguments.Add("--host");
			commandArguments.Add(tool);
		}
		else
		{
			fileName = Processes.FindExecutable(tool);
		}

		if (string.IsNullOrWhiteSpace(fileName))
			return Task.FromResult(OperationResult<ProcessExecution>.Fail("AudioToolMissing", tool + " is not installed"));

		commandArguments.AddRange(arguments);
		return Processes.ExecuteAsync(new ProcessCommand(fileName, commandArguments), cancellationToken);
	}
}
