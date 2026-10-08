using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace Voidstrap.Integrations;

internal sealed record VoidstrapPresenceContext(
	string Owner,
	string Details,
	string State,
	string ImageUrl = "",
	string ImageText = "",
	string ButtonLabel = "",
	string ButtonUrl = "");

internal static class VoidstrapPresence
{
	private static readonly object Sync = new();

	private static VoidstrapPresenceContext? _context;

	public static VoidstrapPresenceContext? Current
	{
		get
		{
			lock (Sync)
			{
				return _context;
			}
		}
	}

	public static void Set(VoidstrapPresenceContext context)
	{
		lock (Sync)
		{
			_context = context;
		}
	}

	public static void Clear(string owner)
	{
		lock (Sync)
		{
			if (_context != null && string.Equals(_context.Owner, owner, StringComparison.Ordinal))
			{
				_context = null;
			}
		}
	}

	public static void SetGame(string owner, string name, string? creator, string? iconUrl, long placeId)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			Clear(owner);
			return;
		}
		string byline = string.IsNullOrWhiteSpace(creator) ? "" : Voidstrap.Utility.RpcText.Mark("By") + " " + creator.Trim();
		Set(new VoidstrapPresenceContext(
			owner,
			Voidstrap.Utility.RpcText.Mark("Viewing") + " " + name.Trim(),
			byline.Length > 0 ? byline : Voidstrap.Utility.RpcText.Mark("Looking at a Roblox game"),
			iconUrl ?? "",
			name.Trim() + (string.IsNullOrWhiteSpace(creator) ? "" : " " + Voidstrap.Utility.RpcText.Mark("by") + " " + creator.Trim()),
			placeId > 0 ? Voidstrap.Utility.RpcText.Mark("View game") : "",
			placeId > 0 ? "https://www.roblox.com/games/" + placeId : ""));
	}

	public static void SetGame(string owner, string name, string? creator, long universeId, long placeId)
	{
		SetGame(owner, name, creator, (string?)null, placeId);
		if (universeId <= 0)
			return;
		_ = ResolveGameIconAsync(owner, name, creator, universeId, placeId);
	}

	private static async System.Threading.Tasks.Task ResolveGameIconAsync(string owner, string name, string? creator, long universeId, long placeId)
	{
		try
		{
			UniverseDetails? details = UniverseDetails.LoadFromCache(universeId);
			if (string.IsNullOrEmpty(details?.Thumbnail?.ImageUrl))
			{
				await UniverseDetails.FetchSingle(universeId).ConfigureAwait(false);
				details = UniverseDetails.LoadFromCache(universeId);
			}
			string? icon = details?.Thumbnail?.ImageUrl;
			if (!IsWebUrl(icon))
				return;
			lock (Sync)
			{
				if (_context == null || !string.Equals(_context.Owner, owner, StringComparison.Ordinal) || !string.Equals(_context.Details, Voidstrap.Utility.RpcText.Mark("Viewing") + " " + name.Trim(), StringComparison.Ordinal))
					return;
			}
			SetGame(owner, name, creator, icon, placeId);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("VoidstrapPresence::ResolveGameIcon", "The game icon could not be loaded: " + ex.Message);
		}
	}

	public static string WebIcon(string? icon, string fallback)
	{
		return IsWebUrl(icon) ? icon! : fallback;
	}

	public static string Clip(string? value, int maxBytes)
	{
		string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
		while (text.Contains("  ", StringComparison.Ordinal))
		{
			text = text.Replace("  ", " ", StringComparison.Ordinal);
		}
		if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
		{
			return text;
		}
		const string ellipsis = "...";
		int budget = maxBytes - Encoding.UTF8.GetByteCount(ellipsis);
		StringBuilder builder = new StringBuilder();
		int used = 0;
		System.Globalization.TextElementEnumerator elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
		while (elements.MoveNext())
		{
			string element = elements.GetTextElement();
			int size = Encoding.UTF8.GetByteCount(element);
			if (used + size > budget)
			{
				break;
			}
			builder.Append(element);
			used += size;
		}
		return builder.ToString().TrimEnd() + ellipsis;
	}

	public static bool IsWebUrl(string? value)
	{
		return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme == Uri.UriSchemeHttps
			&& (value?.Length ?? 0) <= 512;
	}

	public static string PlatformName { get; } = ResolvePlatformName();

	private static string ResolvePlatformName()
	{
		if (OperatingSystem.IsWindows())
		{
			return "Windows";
		}
		if (OperatingSystem.IsMacOS())
		{
			try
			{
				XElement? dictionary = XDocument.Load("/System/Library/CoreServices/SystemVersion.plist").Root?.Element("dict");
				string? version = dictionary?.Elements("key").FirstOrDefault(static key => key.Value == "ProductVersion")?.NextNode is XElement value ? value.Value : null;
				if (Version.TryParse(version, out Version? parsed))
				{
					string name = parsed.Major switch
					{
						11 => "Big Sur",
						12 => "Monterey",
						13 => "Ventura",
						14 => "Sonoma",
						15 => "Sequoia",
						26 => "Tahoe",
						27 => "Golden Gate",
						_ => ""
					};
					return Clip("macOS " + (name.Length > 0 ? name + " " : "") + version, 80);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
			{
				App.Logger.WriteLine("VoidstrapPresence", "The macOS version could not be read: " + ex.Message);
			}
			return "macOS";
		}
		foreach (string path in new[] { "/run/host/os-release", "/etc/os-release", "/usr/lib/os-release" })
		{
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}
				foreach (string line in File.ReadLines(path))
				{
					if (!line.StartsWith("NAME=", StringComparison.Ordinal))
					{
						continue;
					}
					string name = line[5..].Trim().Trim('"', '\'').Trim();
					foreach (string suffix in new[] { " GNU/Linux", " Linux" })
					{
						if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
						{
							name = name[..^suffix.Length].TrimEnd();
						}
					}
					name = Clip(name, 40);
					if (name.Length > 0)
					{
						return name;
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
		return "Linux";
	}
}

internal sealed class DiscordActivityPipe : INamedPipeClient
{
	private readonly INamedPipeClient _inner = OperatingSystem.IsMacOS() ? new MacDiscordPipe() : new ManagedNamedPipeClient();

	private readonly object _gate = new();

	private readonly Func<string?>? _activityName;

	private bool _sealed;

	private bool _hasActivity;

	internal volatile bool Ready;

	private long _activityPid = Environment.ProcessId;

	public DiscordActivityPipe(string? activityName = null)
	{
		_activityName = activityName == null ? null : () => activityName;
	}

	public DiscordActivityPipe(Func<string?> activityName)
	{
		_activityName = activityName;
	}

	public ILogger Logger
	{
		get => _inner.Logger;
		set => _inner.Logger = value;
	}

	public bool IsConnected => _inner.IsConnected;

	[Obsolete("The connected pipe is not neccessary information.")]
	public int ConnectedPipe => _inner.ConnectedPipe;

	public bool Connect(int pipe)
	{
		Ready = false;
		if (_inner.Connect(pipe))
			return true;
		if (_inner is not ManagedNamedPipeClient managed || Voidstrap.Utility.Platform.IsWindows)
			return false;
		foreach (string path in DiscordIpc.FindUnixSockets(pipe))
		{
			if (DiscordIpc.TryConnectPath(managed, path))
				return true;
		}
		return false;
	}

	public bool ReadFrame(out PipeFrame frame) => _inner.ReadFrame(out frame);

	public bool WriteFrame(PipeFrame frame)
	{
		lock (_gate)
		{
			if (frame.Opcode == Opcode.Frame)
			{
				if (_sealed)
					return true;
				frame = Inspect(frame);
			}
			return _inner.WriteFrame(frame);
		}
	}

	public void ClearAndSeal()
	{
		lock (_gate)
		{
			if (_sealed)
				return;
			_sealed = true;
			if (!_hasActivity || !_inner.IsConnected)
				return;

			JsonObject payload = new()
			{
				["cmd"] = "SET_ACTIVITY",
				["args"] = new JsonObject { ["pid"] = _activityPid },
				["nonce"] = Guid.NewGuid().ToString("N")
			};
			try
			{
				if (_inner.WriteFrame(new PipeFrame { Opcode = Opcode.Frame, Message = payload.ToJsonString() }))
					_hasActivity = false;
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
			{
			}
		}
	}

	public void Close()
	{
		lock (_gate)
			_inner.Close();
	}

	public void Dispose()
	{
		lock (_gate)
			_inner.Dispose();
	}

	private PipeFrame Inspect(PipeFrame frame)
	{
		try
		{
			if (JsonNode.Parse(frame.Message) is JsonObject payload
				&& payload["cmd"]?.GetValue<string>() == "SET_ACTIVITY"
				&& payload["args"] is JsonObject arguments)
			{
				if (arguments["pid"] is JsonValue pid && pid.TryGetValue(out long value))
					_activityPid = value;
				_hasActivity = arguments["activity"] is JsonObject;
				string? activityName = _activityName?.Invoke();
				if (!string.IsNullOrWhiteSpace(activityName) && arguments["activity"] is JsonObject activity)
				{
					activity["name"] = activityName;
					frame.Message = payload.ToJsonString();
				}
			}
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
		}
		return frame;
	}
}

internal sealed class MacDiscordPipe : INamedPipeClient
{
	private readonly object _gate = new();
	private readonly ConcurrentQueue<PipeFrame> _frames = new();
	private NetworkStream? _stream;
	private CancellationTokenSource? _reading;
	private bool _disposed;
	private int _pipe = -1;

	public ILogger Logger { get; set; } = new NullLogger();

	public bool IsConnected => Volatile.Read(ref _stream) != null;

	[Obsolete("The connected pipe is not necessary information.")]
	public int ConnectedPipe => _pipe;

	public bool Connect(int pipe)
	{
		lock (_gate)
		{
			if (_disposed)
				return false;
			Close();
			foreach (string path in DiscordIpc.FindUnixSockets(pipe))
			{
				Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
				try
				{
					using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
					socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).AsTask().GetAwaiter().GetResult();
					_stream = new NetworkStream(socket, true) { WriteTimeout = 1000 };
					_pipe = path[^1] - '0';
					_reading = new CancellationTokenSource();
					_ = ReadFramesAsync(_stream, _reading.Token);
					Logger.Info("Connected to the macOS Discord socket");
					return true;
				}
				catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ArgumentException)
				{
					socket.Dispose();
					Logger.Warning("The macOS Discord socket could not connect: {0}", ex.Message);
				}
			}
			return false;
		}
	}

	private async Task ReadFramesAsync(NetworkStream stream, CancellationToken token)
	{
		try
		{
			byte[] header = new byte[8];
			while (!token.IsCancellationRequested)
			{
				await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
				int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
				if (length < 0 || length > PipeFrame.MAX_SIZE)
					throw new InvalidDataException("Discord sent an invalid frame size");
				byte[] data = new byte[length];
				await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
				lock (_gate)
				{
					if (!ReferenceEquals(_stream, stream))
						return;
					if (_frames.Count >= 64)
						throw new InvalidDataException("Discord sent too many pending frames");
					_frames.Enqueue(new PipeFrame { Opcode = (Opcode)BinaryPrimitives.ReadUInt32LittleEndian(header), Data = data });
				}
			}
		}
		catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
		{
			if (!token.IsCancellationRequested)
				Logger.Warning("The macOS Discord connection closed: {0}", ex.Message);
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_stream, stream))
					Close();
			}
		}
	}

	public bool ReadFrame(out PipeFrame frame) => _frames.TryDequeue(out frame);

	public bool WriteFrame(PipeFrame frame)
	{
		lock (_gate)
		{
			if (_stream == null)
				return false;
			try
			{
				frame.WriteStream(_stream);
				return true;
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
			{
				Logger.Warning("The macOS Discord activity could not be written: {0}", ex.Message);
				Close();
				return false;
			}
		}
	}

	public void Close()
	{
		lock (_gate)
		{
			_reading?.Cancel();
			_reading?.Dispose();
			_reading = null;
			_stream?.Dispose();
			_stream = null;
			_frames.Clear();
			_pipe = -1;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			Close();
		}
		GC.SuppressFinalize(this);
	}
}
