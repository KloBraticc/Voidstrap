using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Studio;

public static class StudioBridge
{
	private const string LogTag = "StudioBridge";

	private const int MaxRequestCharacters = 65536;

	public const int Port = 40404;

	private static readonly object _lock = new object();

	private static HttpListener? _listener;

	private static TcpListener? _fallback;

	private static CancellationTokenSource? _cts;

	private static Task? _loopTask;

	private static StudioState? _latest;

	public static bool IsRunning
	{
		get
		{
			lock (_lock)
			{
				return (_listener != null && _listener.IsListening) || _fallback != null;
			}
		}
	}

	public static event Action? StateReceived;

	public static StudioState? GetFreshState(TimeSpan maxAge)
	{
		lock (_lock)
		{
			if (_latest == null)
			{
				return null;
			}
			if (DateTime.UtcNow - _latest.ReceivedUtc > maxAge)
			{
				return null;
			}
			return _latest;
		}
	}

	public static void Start()
	{
		lock (_lock)
		{
			if (_listener != null || _fallback != null)
			{
				return;
			}
			HttpListener? listener = null;
			try
			{
				listener = new HttpListener();
				listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
				listener.Start();
				_listener = listener;
				_cts = new CancellationTokenSource();
				_loopTask = Task.Run(() => LoopAsync(listener, _cts.Token));
				App.Logger.WriteLine(LogTag, $"Listening on 127.0.0.1:{Port}");
				return;
			}
			catch (Exception ex)
			{
				try
				{
					listener?.Close();
				}
				catch
				{
				}
				_listener = null;
				App.Logger.WriteLine(LogTag, "Could not start listener: " + ex.Message);
			}
			StartFallbackCore();
		}
	}

	private static void StartFallbackCore()
	{
		TcpListener? fallback = null;
		try
		{
			fallback = new TcpListener(IPAddress.Loopback, Port);
			fallback.Start();
			_fallback = fallback;
			_cts = new CancellationTokenSource();
			_loopTask = Task.Run(() => FallbackLoopAsync(fallback, _cts.Token));
			App.Logger.WriteLine(LogTag, $"Listening on 127.0.0.1:{Port} without the http service");
		}
		catch (Exception ex)
		{
			try
			{
				fallback?.Stop();
			}
			catch
			{
			}
			_fallback = null;
			_cts?.Dispose();
			_cts = null;
			_loopTask = null;
			App.Logger.WriteLine(LogTag, "Could not start the fallback listener: " + ex.Message);
		}
	}

	private static async Task LoopAsync(HttpListener listener, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			HttpListenerContext context;
			try
			{
				context = await listener.GetContextAsync().WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				break;
			}
			catch (Exception)
			{
				break;
			}
			try
			{
				await HandleRequestAsync(context, token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex3)
			{
				App.Logger.WriteLine(LogTag, "Request error: " + ex3.Message);
				try
				{
					context.Response.StatusCode = 500;
					context.Response.Close();
				}
				catch
				{
				}
			}
		}
	}

	private static async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
	{
		string text = context.Request.Url?.AbsolutePath ?? "";
		if (text.Equals("/ping", StringComparison.OrdinalIgnoreCase))
		{
			WriteJson(context, BuildReply());
		}
		else if (text.Equals("/rpc", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
		{
			if (context.Request.ContentLength64 > MaxRequestCharacters)
			{
				context.Response.StatusCode = 413;
				context.Response.Close();
				return;
			}
			string? text2;
			using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
			{
				timeoutCts.CancelAfter(TimeSpan.FromSeconds(5L));
				using StreamReader reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
				text2 = await ReadBodyAsync(reader, timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (text2 == null)
			{
				context.Response.StatusCode = 413;
				context.Response.Close();
				return;
			}
			bool accepted = Accept(text2);
			WriteJson(context, BuildReply());
			if (accepted)
			{
				RaiseStateReceived();
			}
		}
		else
		{
			context.Response.StatusCode = 404;
			context.Response.Close();
		}
	}

	private static async Task FallbackLoopAsync(TcpListener listener, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception)
			{
				break;
			}
			_ = HandleFallbackClientAsync(client, token);
		}
	}

	private static async Task HandleFallbackClientAsync(TcpClient client, CancellationToken token)
	{
		try
		{
			using (client)
			{
				client.ReceiveTimeout = 5000;
				client.SendTimeout = 5000;
				using NetworkStream stream = client.GetStream();
				using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
				timeoutCts.CancelAfter(TimeSpan.FromSeconds(5L));

				byte[] buffer = new byte[MaxRequestCharacters];
				int total = 0;
				int headerEnd = -1;
				int contentLength = 0;
				string head = "";

				while (total < buffer.Length)
				{
					int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					if (read == 0)
					{
						break;
					}
					total += read;
					if (headerEnd < 0)
					{
						headerEnd = FindHeaderEnd(buffer, total);
						if (headerEnd < 0)
						{
							continue;
						}
						head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
						contentLength = ReadContentLength(head);
					}
					if (headerEnd >= 0 && total - (headerEnd + 4) >= contentLength)
					{
						break;
					}
				}

				if (headerEnd < 0)
				{
					await WriteFallbackAsync(stream, 400, "{}", timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}

				string requestLine = head.Split('\n')[0].Trim();
				string[] parts = requestLine.Split(' ');
				string method = parts.Length > 0 ? parts[0] : "";
				string path = parts.Length > 1 ? parts[1] : "";
				int query = path.IndexOf('?');
				if (query >= 0)
				{
					path = path.Substring(0, query);
				}

				if (path.Equals("/ping", StringComparison.OrdinalIgnoreCase))
				{
					await WriteFallbackAsync(stream, 200, BuildReply(), timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}

				if (!path.Equals("/rpc", StringComparison.OrdinalIgnoreCase) || !method.Equals("POST", StringComparison.OrdinalIgnoreCase))
				{
					await WriteFallbackAsync(stream, 404, "{}", timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}

				int bodyStart = headerEnd + 4;
				int available = Math.Max(0, total - bodyStart);
				int length = contentLength > 0 ? Math.Min(contentLength, available) : available;
				string body = length > 0 ? Encoding.UTF8.GetString(buffer, bodyStart, length) : "";

				bool accepted = Accept(body);
				await WriteFallbackAsync(stream, 200, BuildReply(), timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
				if (accepted)
				{
					RaiseStateReceived();
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Request error: " + ex.Message);
		}
	}

	private static int FindHeaderEnd(byte[] buffer, int length)
	{
		for (int i = 3; i < length; i++)
		{
			if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r')
			{
				return i - 3;
			}
		}
		return -1;
	}

	private static int ReadContentLength(string headers)
	{
		foreach (string line in headers.Split('\n'))
		{
			int separator = line.IndexOf(':');
			if (separator <= 0)
			{
				continue;
			}
			if (!line.AsSpan(0, separator).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (int.TryParse(line.AsSpan(separator + 1).Trim(), out int value) && value >= 0 && value <= MaxRequestCharacters)
			{
				return value;
			}
			return 0;
		}
		return 0;
	}

	private static async Task WriteFallbackAsync(NetworkStream stream, int status, string json, CancellationToken token)
	{
		byte[] payload = Encoding.UTF8.GetBytes(json);
		string head = "HTTP/1.1 " + status + " OK\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n";
		byte[] headBytes = Encoding.ASCII.GetBytes(head);
		await stream.WriteAsync(headBytes, token).ConfigureAwait(continueOnCapturedContext: false);
		await stream.WriteAsync(payload, token).ConfigureAwait(continueOnCapturedContext: false);
		await stream.FlushAsync(token).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static string BuildReply()
	{
		return "{\"ok\":true,\"app\":\"voidstrap\",\"version\":\"" + App.Version + "\",\"palette\":" + StudioTheme.GetPaletteJson() + "}";
	}

	private static bool Accept(string body)
	{
		StudioState? studioState = Parse(body);
		if (studioState == null)
		{
			return false;
		}
		studioState.ReceivedUtc = DateTime.UtcNow;
		lock (_lock)
		{
			if (_listener == null && _fallback == null)
			{
				return false;
			}
			_latest = studioState;
		}
		return true;
	}

	private static void RaiseStateReceived()
	{
		try
		{
			StateReceived?.Invoke();
		}
		catch
		{
		}
	}

	private static StudioState? Parse(string body)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(body);
			JsonElement rootElement = jsonDocument.RootElement;
			return new StudioState
			{
				Sharing = GetBool(rootElement, "sharing", fallback: true),
				Place = GetString(rootElement, "place"),
				PlaceId = GetLong(rootElement, "placeId"),
				UniverseId = GetLong(rootElement, "universeId"),
				Creator = GetString(rootElement, "creator"),
				Script = GetString(rootElement, "script"),
				ScriptLines = GetInt(rootElement, "scriptLines"),
				Mode = GetString(rootElement, "mode"),
				Selection = GetInt(rootElement, "selection"),
				SelectionClass = GetString(rootElement, "selectionClass"),
				Custom = GetString(rootElement, "custom")
			};
		}
		catch
		{
			return null;
		}
	}

	private static async Task<string?> ReadBodyAsync(StreamReader reader, CancellationToken token)
	{
		char[] buffer = new char[4096];
		StringBuilder body = new StringBuilder();
		while (true)
		{
			int num = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(continueOnCapturedContext: false);
			if (num == 0)
			{
				return body.ToString();
			}
			if (body.Length + num > MaxRequestCharacters)
			{
				break;
			}
			body.Append(buffer, 0, num);
		}
		return null;
	}

	private static bool GetBool(JsonElement root, string name, bool fallback)
	{
		if (!root.TryGetProperty(name, out var value))
		{
			return fallback;
		}
		if (value.ValueKind == JsonValueKind.True)
		{
			return true;
		}
		if (value.ValueKind == JsonValueKind.False)
		{
			return false;
		}
		return fallback;
	}

	private static string GetString(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return "";
		}
		return value.GetString() ?? "";
	}

	private static int GetInt(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var value2))
		{
			return 0;
		}
		return value2;
	}

	private static long GetLong(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var value2))
		{
			return 0L;
		}
		return value2;
	}

	private static void WriteJson(HttpListenerContext context, string json)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(json);
		context.Response.StatusCode = 200;
		context.Response.ContentType = "application/json";
		context.Response.ContentLength64 = bytes.Length;
		context.Response.OutputStream.Write(bytes, 0, bytes.Length);
		context.Response.Close();
	}

	public static void Shutdown()
	{
		HttpListener? listener;
		TcpListener? fallback;
		CancellationTokenSource? cts;
		Task? loopTask;
		lock (_lock)
		{
			listener = _listener;
			fallback = _fallback;
			cts = _cts;
			loopTask = _loopTask;
			_listener = null;
			_fallback = null;
			_cts = null;
			_loopTask = null;
			_latest = null;
		}
		try
		{
			cts?.Cancel();
		}
		catch
		{
		}
		try
		{
			listener?.Stop();
		}
		catch
		{
		}
		try
		{
			listener?.Close();
		}
		catch
		{
		}
		try
		{
			fallback?.Stop();
		}
		catch
		{
		}
		try
		{
			loopTask?.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			App.Logger.WriteLine(LogTag, "Shutdown failed: " + ex2.Message);
		}
		try
		{
			cts?.Dispose();
		}
		catch
		{
		}
	}
}
