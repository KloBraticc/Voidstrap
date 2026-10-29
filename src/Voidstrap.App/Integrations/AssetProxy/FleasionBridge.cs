using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.AssetProxy;

internal static class FleasionBridge
{
	private static readonly System.Threading.Lock Gate = new();

	internal static readonly System.Threading.SemaphoreSlim ConfigGate = new(1, 1);

	private static string StatePath => Path.Combine(Paths.AssetProxy, "FleasionRouting.json");
	internal static string ConfigDirectory => Voidstrap.Utility.Platform.IsLinux
		? Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "Fleasion")
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleasionNT");
	internal static bool HasPendingRestore => File.Exists(StatePath);
	internal static bool IsRequested
	{
		get
		{
			try
			{
				if (ReadObject(Path.Combine(ConfigDirectory, "settings.json"))["proxy_features_enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out bool value) && !value)
					return false;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
			{
				return false;
			}
			if (App.Settings.Prop.Fleasion)
				return ResolveExecutable() != null;
			Process[] processes = FindProcesses();
			try { return processes.Length > 0 && ResolveExecutable() != null; }
			finally
			{
				foreach (Process process in processes)
					process.Dispose();
			}
		}
	}
	internal static bool ManagesPlayerLaunch
	{
		get
		{
			if (!IsRequested)
				return false;
			try
			{
				JsonObject settings = ReadObject(Path.Combine(ConfigDirectory, "settings.json"));
				return !string.Equals(settings["proxy_mode"]?.GetValue<string>(), "hosts", StringComparison.OrdinalIgnoreCase)
					&& settings["proxy_features_enabled"]?.GetValue<bool>() != false;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
			{
				return false;
			}
		}
	}

	internal static string? ResolveExecutable()
	{
		string[] names = Voidstrap.Utility.Platform.IsLinux ? ["Fleasion", "fleasion", "Fleasion.AppImage"] : ["Fleasion.exe"];
		foreach (string name in names)
		{
			string installed = Path.Combine(Paths.Fleasion, name);
			if (File.Exists(installed))
				return installed;
		}
		if (Voidstrap.Utility.Platform.IsLinux && Directory.Exists(Paths.Fleasion))
		{
			string? installed = Directory.EnumerateFiles(Paths.Fleasion, "Fleasion*-Linux*").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
			if (installed != null)
				return installed;
		}
		Process[] processes = FindProcesses();
		try
		{
			foreach (Process process in processes)
			{
				try
				{
					string? executable = process.MainModule?.FileName;
					if (executable != null && File.Exists(executable))
						return executable;
				}
				catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
				{
				}
			}
		}
		finally
		{
			foreach (Process process in processes)
				process.Dispose();
		}
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
				foreach (string name in names)
				{
					string candidate = Path.Combine(directory, name);
					if (File.Exists(candidate))
						return candidate;
				}
		}
		return null;
	}

	private static Process[] FindProcesses()
	{
		List<Process> matches = [];
		foreach (Process process in Process.GetProcesses())
		{
			bool keep = false;
			try
			{
				string name = process.ProcessName;
				keep = name.Equals("Fleasion", StringComparison.OrdinalIgnoreCase)
					|| name.StartsWith("Fleasion-", StringComparison.OrdinalIgnoreCase)
					|| name.StartsWith("Fleasion.App", StringComparison.OrdinalIgnoreCase);
				if (keep)
					matches.Add(process);
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
			{
			}
			finally
			{
				if (!keep)
					process.Dispose();
			}
		}
		return [.. matches];
	}

	private static JsonObject ReadObject(string path)
	{
		if (!File.Exists(path))
			return new JsonObject();
		if (new FileInfo(path).Length > 8388608)
			throw new InvalidDataException("The Fleasion settings file is too large");
		return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Fleasion settings must be a JSON object");
	}

	private static void WriteObject(string path, JsonObject value)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporary, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static void StartInstance(string executable)
	{
		ProcessStartInfo info = new(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
		info.ArgumentList.Add("--no-dashboard");
		using Process? process = Process.Start(info);
		if (process == null)
			throw new IOException("Fleasion could not be started");
	}

	private static async Task SendCommandAsync(string command, CancellationToken token)
	{
		if (Voidstrap.Utility.Platform.IsWindows)
		{
			using NamedPipeClientStream pipe = new(".", "FleasionSingleInstanceControl", PipeDirection.Out, PipeOptions.Asynchronous);
			await pipe.ConnectAsync(1000, token).ConfigureAwait(false);
			await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), token).ConfigureAwait(false);
			await pipe.FlushAsync(token).ConfigureAwait(false);
		}
		else
		{
			using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(Path.GetTempPath(), "FleasionSingleInstanceControl")), token).ConfigureAwait(false);
			using NetworkStream stream = new(socket, false);
			await stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), token).ConfigureAwait(false);
		}
	}

	private static async Task StopInstanceAsync(CancellationToken token)
	{
		Process[] processes = FindProcesses();
		try
		{
			if (processes.Length == 0)
				return;
			await SendCommandAsync("quit-preserve-env-player", token).ConfigureAwait(false);
			foreach (Process process in processes)
				await process.WaitForExitAsync(token).ConfigureAwait(false);
		}
		finally
		{
			foreach (Process process in processes)
				process.Dispose();
		}
	}

	internal static async Task<Process> FollowPlayerAsync(Process initial, string executable, CancellationToken token)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(30);
		DateTime started = initial.StartTime.ToUniversalTime();
		Task exited = initial.WaitForExitAsync(token);
		if (await Task.WhenAny(exited, Task.Delay(15000, token)).ConfigureAwait(false) != exited)
		{
			token.ThrowIfCancellationRequested();
			return initial;
		}
		token.ThrowIfCancellationRequested();
		if ((uint)initial.ExitCode >= 0xC0000000u)
			return initial;
		while (DateTime.UtcNow < deadline)
		{
			Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
			Process? selected = null;
			try
			{
				foreach (Process process in processes)
				{
					try
					{
						if (!process.HasExited && process.StartTime.ToUniversalTime() >= started && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
						{
							selected = process;
							initial.Dispose();
							App.Logger?.WriteLine("FleasionBridge", "Following the Roblox process started by Fleasion");
							return process;
						}
					}
					catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
					{
					}
				}
			}
			finally
			{
				foreach (Process process in processes)
					if (process != selected)
						process.Dispose();
			}
			await Task.Delay(500, token).ConfigureAwait(false);
		}
		token.ThrowIfCancellationRequested();
		return initial;
	}

	private static string? RestoreSettings()
	{
		if (!File.Exists(StatePath))
			return null;
		JsonObject state = ReadObject(StatePath);
		string settingsPath = state["settings_path"]!.GetValue<string>();
		if (!string.Equals(settingsPath, Path.Combine(ConfigDirectory, "settings.json"), StringComparison.Ordinal))
			throw new InvalidDataException("The Fleasion recovery file points to another settings folder");
		JsonObject settings = ReadObject(settingsPath);
		JsonObject original = state["original"]!.AsObject();
		foreach (var property in state["applied"]!.AsObject())
		{
			if (!JsonNode.DeepEquals(settings[property.Key], property.Value))
				continue;
			if (original.TryGetPropertyValue(property.Key, out JsonNode? value))
				settings[property.Key] = value?.DeepClone();
			else
				settings.Remove(property.Key);
		}
		WriteObject(settingsPath, settings);
		File.Delete(StatePath);
		return state["executable"]?.GetValue<string>();
	}

	internal static void Restore()
	{
		ConfigGate.Wait();
		try
		{
			RestoreCore();
		}
		finally
		{
			ConfigGate.Release();
		}
	}

	private static void RestoreCore()
	{
		lock (Gate)
		{
			if (!File.Exists(StatePath))
			{
				return;
			}
			try
			{
				using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(3));
				StopInstanceAsync(deadline.Token).GetAwaiter().GetResult();
				string? executable = RestoreSettings();
				if (executable != null && File.Exists(executable))
					StartInstance(executable);
				App.Logger?.WriteLine("FleasionBridge", "Fleasion routing restored");
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("FleasionBridge", "Fleasion routing recovery is pending: " + ex.Message);
			}
		}
	}
}
