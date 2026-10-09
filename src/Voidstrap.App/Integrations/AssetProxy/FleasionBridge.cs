using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.AssetProxy;

internal static class FleasionBridge
{
	private static readonly System.Threading.Lock Gate = new();
	private static string _authorization = "";
	private static string StatePath => Path.Combine(Paths.AssetProxy, "FleasionRouting.json");
	internal static string ConfigDirectory => Voidstrap.Utility.Platform.IsLinux
		? Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "Fleasion")
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleasionNT");
	internal static bool IsActive => Volatile.Read(ref _authorization).Length > 0;
	internal static bool IsRequested
	{
		get
		{
			if (IsActive)
				return true;
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

	internal static bool IsAuthorized(string authorization)
	{
		string expected = Volatile.Read(ref _authorization);
		return expected.Length == 0 || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(authorization));
	}

	internal static async Task EnableAsync(Uri address, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		string executable = ResolveExecutable() ?? throw new FileNotFoundException("Fleasion could not be found");
		ReadObject(Path.Combine(ConfigDirectory, "settings.json"));
		await StopInstanceAsync(token).ConfigureAwait(false);
		token.ThrowIfCancellationRequested();
		lock (Gate)
		{
			RestoreSettings();
			string settingsPath = Path.Combine(ConfigDirectory, "settings.json");
			JsonObject settings = ReadObject(settingsPath);
			ConfigureUpstream(settings);
			JsonObject applied = new()
			{
				["upstream_transport_mode"] = "http_connect",
				["upstream_http_connect_host"] = "127.0.0.1",
				["upstream_http_connect_port"] = address.Port,
				["upstream_http_connect_username"] = "Voidstrap",
				["upstream_http_connect_password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
			};
			JsonObject original = new();
			foreach (var property in applied)
			{
				if (settings.TryGetPropertyValue(property.Key, out JsonNode? value))
					original[property.Key] = value?.DeepClone();
				settings[property.Key] = property.Value?.DeepClone();
			}
			WriteObject(StatePath, new JsonObject { ["settings_path"] = settingsPath, ["executable"] = executable, ["original"] = original, ["applied"] = applied });
			WriteObject(settingsPath, settings);
			Volatile.Write(ref _authorization, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Voidstrap:" + applied["upstream_http_connect_password"]!.GetValue<string>())));
		}
		try
		{
			string trustPath = Voidstrap.Utility.Platform.IsLinux
				? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".var", "app", "org.vinegarhq.Sober", "data", "sober", "exe", "ssl", "cacert.pem")
				: Path.Combine(new Voidstrap.AppData.RobloxPlayerData().Directory, "ssl", "cacert.pem");
			if (!File.Exists(trustPath) || AssetProxyCA.RootCa == null)
				throw new IOException("The Roblox certificate bundle could not be found for Fleasion");
			string bundlePath = Path.Combine(Paths.AssetProxy, "Certificates", "fleasion-upstream.pem");
			Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
			File.WriteAllText(bundlePath, File.ReadAllText(trustPath) + Environment.NewLine + AssetProxyCA.RootCa.ExportCertificatePem());
			token.ThrowIfCancellationRequested();
			StartInstance(executable, bundlePath);
			App.Logger?.WriteLine("FleasionBridge", "Fleasion now forwards through AssetWarp");
		}
		catch
		{
			Restore();
			throw;
		}
	}

	private static JsonObject ReadObject(string path)
	{
		if (!File.Exists(path))
			return new JsonObject();
		if (new FileInfo(path).Length > 8388608)
			throw new InvalidDataException("The Fleasion settings file is too large");
		return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Fleasion settings must be a JSON object");
	}

	private static void ConfigureUpstream(JsonObject settings)
	{
		if (App.Settings.Prop.ProxyConnectorType != 0)
			return;
		string mode = settings["upstream_transport_mode"]?.GetValue<string>() ?? "auto";
		if (mode == "http_connect")
		{
			UpstreamConnector.ConnectorType = UpstreamConnectorType.HttpConnect;
			UpstreamConnector.HttpConnectProxyHost = settings["upstream_http_connect_host"]?.GetValue<string>() ?? "";
			UpstreamConnector.HttpConnectProxyPort = settings["upstream_http_connect_port"]?.GetValue<int>() ?? 0;
			UpstreamConnector.HttpConnectProxyUsername = settings["upstream_http_connect_username"]?.GetValue<string>() ?? "";
			UpstreamConnector.HttpConnectProxyPassword = settings["upstream_http_connect_password"]?.GetValue<string>() ?? "";
		}
		else if (mode == "socks5")
		{
			UpstreamConnector.ConnectorType = UpstreamConnectorType.Socks5;
			UpstreamConnector.Socks5ProxyHost = settings["upstream_socks5_host"]?.GetValue<string>() ?? "";
			UpstreamConnector.Socks5ProxyPort = settings["upstream_socks5_port"]?.GetValue<int>() ?? 0;
			UpstreamConnector.Socks5ProxyUsername = settings["upstream_socks5_username"]?.GetValue<string>() ?? "";
			UpstreamConnector.Socks5ProxyPassword = settings["upstream_socks5_password"]?.GetValue<string>() ?? "";
		}
		else if (mode is "system_proxy" or "auto")
		{
			Uri target = new("https://assetdelivery.roblox.com");
			Uri? proxy = HttpClient.DefaultProxy.GetProxy(target);
			if (proxy != null && proxy != target && proxy.Scheme == "http")
			{
				UpstreamConnector.ConnectorType = mode == "auto" ? UpstreamConnectorType.Auto : UpstreamConnectorType.HttpConnect;
				UpstreamConnector.HttpConnectProxyHost = proxy.Host;
				UpstreamConnector.HttpConnectProxyPort = proxy.Port;
			}
		}
	}

	private static void WriteObject(string path, JsonObject value)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temporary = path + ".tmp";
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

	private static void StartInstance(string executable, string? trustBundle = null)
	{
		ProcessStartInfo info = new(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
		info.ArgumentList.Add("--no-dashboard");
		if (trustBundle != null)
			info.Environment["SSL_CERT_FILE"] = trustBundle;
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
		Volatile.Write(ref _authorization, "");
		return state["executable"]?.GetValue<string>();
	}

	internal static void Restore()
	{
		lock (Gate)
		{
			if (!File.Exists(StatePath))
			{
				Volatile.Write(ref _authorization, "");
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
