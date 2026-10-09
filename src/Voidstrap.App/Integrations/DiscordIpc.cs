using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DiscordRPC;
using DiscordRPC.IO;
using DiscordRPC.Logging;
using DiscordRPC.Message;

namespace Voidstrap.Integrations;

internal static class DiscordIpc
{
	internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

	internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

	private static readonly ConditionalWeakTable<DiscordRpcClient, DiscordActivityPipe> Pipes = new();

	private static readonly PosixSignalRegistration? TerminationSignal;
	private static readonly PosixSignalRegistration? InterruptSignal;

	static DiscordIpc()
	{
		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		if (OperatingSystem.IsMacOS())
		{
			TerminationSignal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminationSignal);
			InterruptSignal = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnTerminationSignal);
		}
	}

	internal static DiscordRpcClient CreateClient(string applicationId, int pipe, ILogger? logger = null, string? activityName = null)
	{
		return CreateClient(applicationId, pipe, new DiscordActivityPipe(activityName), logger);
	}

	internal static DiscordRpcClient CreateClient(string applicationId, int pipe, Func<string?> activityName, ILogger? logger = null)
	{
		return CreateClient(applicationId, pipe, new DiscordActivityPipe(activityName), logger);
	}

	private static DiscordRpcClient CreateClient(string applicationId, int pipe, DiscordActivityPipe transport, ILogger? logger)
	{
		DiscordRpcClient client = new(applicationId, pipe, logger, true, transport);
		Pipes.AddOrUpdate(client, transport);
		client.OnReady += OnClientReady;
		return client;
	}

	private static void OnClientReady(object sender, ReadyMessage e)
	{
		if (sender is DiscordRpcClient client && Pipes.TryGetValue(client, out DiscordActivityPipe? transport))
			transport.Ready = true;
	}

	internal static bool IsReady(DiscordRpcClient? client)
	{
		return client is { IsInitialized: true, IsDisposed: false }
			&& Pipes.TryGetValue(client, out DiscordActivityPipe? transport)
			&& transport.Ready && transport.IsConnected;
	}

	internal static void Close(DiscordRpcClient? client)
	{
		if (client == null)
			return;

		if (Pipes.TryGetValue(client, out DiscordActivityPipe? transport))
			transport.ClearAndSeal();
		client.OnReady -= OnClientReady;
		try
		{
			client.Dispose();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
		}
		Pipes.Remove(client);
	}

	private static void OnProcessExit(object? sender, EventArgs e)
	{
		AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		ClearActivities();
		TerminationSignal?.Dispose();
		InterruptSignal?.Dispose();
	}

	private static void OnTerminationSignal(PosixSignalContext context) => ClearActivities();

	private static void ClearActivities()
	{
		foreach (KeyValuePair<DiscordRpcClient, DiscordActivityPipe> entry in Pipes.ToArray())
			entry.Value.ClearAndSeal();
	}

	private static readonly string[] SandboxDirectories =
	{
		"app/com.discordapp.Discord",
		"snap.discord"
	};

	private static readonly string[] ClientNames =
	{
		"discord",
		"vesktop",
		"vencord",
		"equibop",
		"equicord",
		"legcord",
		"armcord",
		"webcord",
		"goofcord",
		"dorion",
		"abaddon",
		"discord-screenaudio"
	};

	private static readonly MethodInfo? AttemptConnectionMethod = typeof(ManagedNamedPipeClient).GetMethod("AttemptConnection", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);

	private static readonly MethodInfo? BeginReadStreamMethod = typeof(ManagedNamedPipeClient).GetMethod("BeginReadStream", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

	internal const string ClientWithoutRichPresenceMessage = "Discord is running, but Voidstrap could not reach its Rich Presence connection. If you use Vesktop, Equibop or another modified client, turn on its Rich Presence (arRPC) option, then try again.";

	internal static string MissingPipeMessage => !Voidstrap.Utility.Platform.IsWindows && IsDiscordClientRunning()
		? ClientWithoutRichPresenceMessage
		: "Discord is not running.";

	internal static bool TryFindPipe(out int pipe)
	{
		if (Voidstrap.Utility.Platform.IsWindows)
			return TryFindPipeIn(@"\\.\pipe\", out pipe);

		foreach (string socket in FindUnixSockets(-1))
		{
			if (TryParsePipe(Path.GetFileName(socket), out pipe))
				return true;
		}

		pipe = -1;
		return false;
	}

	internal static IEnumerable<string> FindUnixSockets(int pipe)
	{
		foreach (string directory in UnixSocketDirectories())
		{
			for (int candidate = 0; candidate <= 9; candidate++)
			{
				if (pipe >= 0 && candidate != pipe)
					continue;
				string path = Path.Combine(directory, "discord-ipc-" + candidate);
				if (IsListening(path))
					yield return path;
			}
		}
	}

	internal static bool TryConnectPath(ManagedNamedPipeClient client, string path)
	{
		if (AttemptConnectionMethod is null || BeginReadStreamMethod is null)
			return false;
		try
		{
			if (AttemptConnectionMethod.Invoke(client, new object[] { path }) is true)
			{
				BeginReadStreamMethod.Invoke(client, null);
				return true;
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
		}
		return false;
	}

	private static List<string> UnixSocketDirectories()
	{
		List<string> directories = new List<string>();
		HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
		string?[] roots =
		{
			Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
			Environment.GetEnvironmentVariable("TMPDIR"),
			Environment.GetEnvironmentVariable("TMP"),
			Environment.GetEnvironmentVariable("TEMP"),
			Path.GetTempPath(),
			"/tmp"
		};
		foreach (string? root in roots)
			AddRoot(directories, unique, root);
		foreach (string? root in roots)
			AddSandboxRoots(directories, unique, root);
		return directories;
	}

	private static void AddSandboxRoots(List<string> directories, HashSet<string> unique, string? root)
	{
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
			return;

		foreach ((string parent, string suffix) in new[] { ("app", ""), (".flatpak", "xdg-run") })
		{
			try
			{
				string container = Path.Combine(root, parent);
				if (!Directory.Exists(container))
					continue;
				foreach (string application in Directory.EnumerateDirectories(container).Order(StringComparer.Ordinal))
					AddDirectory(directories, unique, suffix.Length == 0 ? application : Path.Combine(application, suffix));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		try
		{
			foreach (string snap in Directory.EnumerateDirectories(root, "snap.*").Order(StringComparer.Ordinal))
				AddDirectory(directories, unique, snap);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static bool IsListening(string path)
	{
		try
		{
			if (!File.Exists(path))
				return false;
			using Socket socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(500));
			socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).AsTask().GetAwaiter().GetResult();
			return true;
		}
		catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException)
		{
			return false;
		}
	}

	private static bool TryParsePipe(string name, out int pipe)
	{
		pipe = -1;
		return name.Length == 13
			&& name.StartsWith("discord-ipc-", StringComparison.Ordinal)
			&& int.TryParse(name.AsSpan(12), out pipe)
			&& pipe is >= 0 and <= 9;
	}

	internal static bool IsDiscordClientRunning()
	{
		if (Voidstrap.Utility.Platform.IsMacOS)
		{
			Process[] clients = Process.GetProcesses();
			bool running = false;
			foreach (Process client in clients)
			{
				try
				{
					string name = client.ProcessName.ToLowerInvariant();
					if (ClientNames.Any(candidate => name.Contains(candidate, StringComparison.Ordinal)))
						running = true;
				}
				catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
				}
				finally
				{
					client.Dispose();
				}
			}
			return running;
		}
		string[] processes;
		try
		{
			processes = Directory.GetDirectories("/proc");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}

		foreach (string directory in processes)
		{
			string id = Path.GetFileName(directory);
			if (id.Length == 0 || !char.IsAsciiDigit(id[0]) || id == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
				continue;
			try
			{
				string command = File.ReadAllText(Path.Combine(directory, "cmdline"));
				int end = command.IndexOf('\0');
				string program = Path.GetFileName(end < 0 ? command : command[..end]).ToLowerInvariant();
				if (program.Length > 0 && ClientNames.Any(name => program.Contains(name, StringComparison.Ordinal)))
					return true;

				string group = File.ReadAllText(Path.Combine(directory, "cgroup")).ToLowerInvariant();
				if (group.Contains("app-flatpak-", StringComparison.Ordinal) && ClientNames.Any(name => group.Contains(name, StringComparison.Ordinal)))
					return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		return false;
	}

	private static bool TryFindPipeIn(string directory, out int pipe)
	{
		pipe = -1;
		try
		{
			foreach (string path in Directory.EnumerateFileSystemEntries(directory, "discord-ipc-*", SearchOption.TopDirectoryOnly))
			{
				string name = Path.GetFileName(path);
				if (name.Length == 13 &&
					name.StartsWith("discord-ipc-", StringComparison.Ordinal) &&
					int.TryParse(name.AsSpan(12), out int candidate) &&
					candidate is >= 0 and <= 9)
				{
					pipe = candidate;
					return true;
				}
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}

		return false;
	}

	private static void AddRoot(List<string> directories, HashSet<string> unique, string? root)
	{
		if (string.IsNullOrWhiteSpace(root))
			return;

		AddDirectory(directories, unique, root);
		foreach (string child in SandboxDirectories)
			AddDirectory(directories, unique, Path.Combine(root, child));
	}

	private static void AddDirectory(List<string> directories, HashSet<string> unique, string directory)
	{
		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(directory);
		}
		catch
		{
			return;
		}

		if (unique.Add(fullPath) && Directory.Exists(fullPath))
			directories.Add(fullPath);
	}
}
