using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Voidstrap.Platform.Linux;

public enum RosemaryApplication
{
	Player,
	Studio
}

public sealed record RosemaryRobloxOptions(string? Channel = null, bool UpdateRoblox = true, bool ForceReinstall = false);

public static partial class LinuxRosemary
{
	public const string ProjectUrl = "https://github.com/microlift/rosemary";

	private const string LatestReleaseUrl = "https://api.github.com/repos/microlift/rosemary/releases/latest";
	private const string DownloadPrefix = "https://github.com/microlift/rosemary/releases/download/";
	private const string VersionFileName = "version";

	private static readonly string[] CdnHosts = ["https://setup.rbxcdn.com", "https://setup-aws.rbxcdn.com", "https://setup-ak.rbxcdn.com"];
	private static readonly string[] ClientSettingsHosts = ["https://clientsettingscdn.roblox.com", "https://clientsettings.roblox.com"];

	private static readonly HttpClient Http = CreateClient();
	private static readonly SemaphoreSlim RuntimeGate = new(1, 1);
	private static readonly SemaphoreSlim RobloxGate = new(1, 1);
	private static volatile string? _runtimeActivity;
	private static volatile string? _robloxActivity;

	private static async Task EnterAsync(SemaphoreSlim gate, Func<string?> activity, string waiting, Action<string>? status, CancellationToken cancellationToken)
	{
		if (gate.Wait(0))
			return;
		string? shown = null;
		while (!await gate.WaitAsync(250, cancellationToken).ConfigureAwait(false))
		{
			string message = activity() ?? waiting;
			if (message == shown)
				continue;
			shown = message;
			status?.Invoke(message);
		}
	}

	public static bool IsSupportedHost => RuntimeInformation.OSArchitecture == Architecture.X64;

	public static string Root
	{
		get
		{
			string? data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
			if (string.IsNullOrWhiteSpace(data) || !Path.IsPathRooted(data))
				data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
			return Path.Combine(data, "voidstrap", "rosemary");
		}
	}

	public static string RuntimeRoot => Path.Combine(Root, "runtime");

	public static string PrefixPath => Path.Combine(Root, "prefix");

	public static string ApplicationsRoot => Path.Combine(Root, "apps");

	public static string GuestHome => Path.Combine(PrefixPath, "root", "Users", string.IsNullOrWhiteSpace(Environment.UserName) ? "user" : Environment.UserName);

	public static string GuestRobloxRoot => Path.Combine(GuestHome, "Library", "Roblox");

	public static string GuestLogs => Path.Combine(GuestHome, "Library", "Logs", "Roblox");

	public static string GuestCookieStore => Path.Combine(GuestHome, "Library", "Cookies", "com.roblox.RobloxPlayer.cookies.tsv");

	public static string? ReadCookie(string name, string domain)
	{
		try
		{
			if (!File.Exists(GuestCookieStore))
				return null;
			string? found = null;
			foreach (string line in File.ReadLines(GuestCookieStore))
			{
				string[] fields = line.Split('\t');
				if (fields.Length < 3 || !string.Equals(fields[0], name, StringComparison.Ordinal))
					continue;
				string host = fields[2].TrimStart('.');
				if (!string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) && !host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
					continue;
				if (fields[1].Length > 0)
					found = fields[1];
			}
			return found;
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

	public static string ApplicationPath(RosemaryApplication application) =>
		Path.Combine(ApplicationsRoot, application == RosemaryApplication.Player ? "Roblox.app" : "RobloxStudio.app");

	public static string? InstalledRuntimeVersion => ReadText(Path.Combine(RuntimeRoot, VersionFileName));

	public static string? RuntimeExecutable
	{
		get
		{
			string? version = InstalledRuntimeVersion;
			if (string.IsNullOrEmpty(version))
				return null;
			string path = Path.Combine(RuntimeRoot, version, "AppRun");
			return File.Exists(path) ? path : null;
		}
	}

	public static bool IsRuntimeInstalled => RuntimeExecutable != null;

	public static bool IsApplicationInstalled(RosemaryApplication application) =>
		File.Exists(Path.Combine(ApplicationPath(application), "Contents", "Info.plist"));

	public static string? InstalledRobloxVersion(RosemaryApplication application) =>
		ReadText(ApplicationPath(application) + ".version")?.Split('|')[0];

	public static IReadOnlyList<string> LogDirectories()
	{
		List<string> found = [];
		string users = Path.Combine(PrefixPath, "root", "Users");
		try
		{
			if (Directory.Exists(users))
			{
				foreach (string user in Directory.EnumerateDirectories(users))
				{
					string logs = Path.Combine(user, "Library", "Logs", "Roblox");
					if (Directory.Exists(logs))
						found.Add(logs);
				}
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		return found;
	}

	public static async Task<OperationResult<string>> EnsureRuntimeAsync(Action<string>? status, CancellationToken cancellationToken = default)
	{
		if (!IsSupportedHost)
			return OperationResult<string>.Fail("RosemaryUnsupported", "Rosemary only publishes builds for x86_64 Linux, this machine is " + RuntimeInformation.OSArchitecture);

		await EnterAsync(RuntimeGate, () => _runtimeActivity, "Waiting for the Rosemary update running in the background", status, cancellationToken).ConfigureAwait(false);
		Action<string> report = message =>
		{
			_runtimeActivity = message;
			status?.Invoke(message);
		};
		try
		{
			string? installed = RuntimeExecutable;
			report("Checking for Rosemary updates");
			(string Version, string AppImageUrl, string ChecksumsUrl, string AppImageName) latest;
			try
			{
				latest = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return installed != null
					? OperationResult<string>.Success(installed)
					: OperationResult<string>.Fail("RosemaryReleaseUnavailable", "The latest Rosemary release could not be checked: " + ex.Message);
			}

			if (installed != null && string.Equals(InstalledRuntimeVersion, latest.Version, StringComparison.Ordinal))
				return OperationResult<string>.Success(installed);

			try
			{
				string path = await InstallRuntimeAsync(latest.Version, latest.AppImageUrl, latest.ChecksumsUrl, latest.AppImageName, report, cancellationToken).ConfigureAwait(false);
				return OperationResult<string>.Success(path);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return installed != null
					? OperationResult<string>.Success(installed)
					: OperationResult<string>.Fail("RosemaryInstallFailed", "Rosemary could not be installed: " + ex.Message);
			}
		}
		finally
		{
			_runtimeActivity = null;
			RuntimeGate.Release();
		}
	}

	public static async Task<OperationResult<string>> EnsureRobloxAsync(RosemaryApplication application, RosemaryRobloxOptions options, Action<string>? status, CancellationToken cancellationToken = default)
	{
		bool player = application == RosemaryApplication.Player;
		string displayName = player ? "Roblox" : "Roblox Studio";
		string target = ApplicationPath(application);
		bool installed = IsApplicationInstalled(application);
		if (installed && !options.ForceReinstall && !options.UpdateRoblox)
			return OperationResult<string>.Success(target);

		await EnterAsync(RobloxGate, () => _robloxActivity, "Waiting for the Roblox download running in the background", status, cancellationToken).ConfigureAwait(false);
		Action<string> report = message =>
		{
			_robloxActivity = message;
			status?.Invoke(message);
		};
		try
		{
			report("Checking for " + displayName + " updates");
			(string Version, string Upload, string Channel) latest;
			try
			{
				latest = await GetLatestRobloxAsync(player ? "MacPlayer" : "MacStudio", options.Channel, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return installed
					? OperationResult<string>.Success(target)
					: OperationResult<string>.Fail("RobloxVersionUnavailable", "The latest macOS " + displayName + " version could not be checked: " + ex.Message);
			}

			string stamp = latest.Version + "|" + latest.Upload;
			if (installed && !options.ForceReinstall && string.Equals(ReadText(target + ".version"), stamp, StringComparison.Ordinal))
				return OperationResult<string>.Success(target);

			string package = player ? "RobloxPlayer.zip" : "RobloxStudioApp.zip";
			string channelPath = IsProduction(latest.Channel) ? "" : "channel/" + latest.Channel.ToLowerInvariant() + "/";
			string relative = channelPath + "mac/" + latest.Upload + "-" + package;
			Directory.CreateDirectory(ApplicationsRoot);
			string archive = Path.Combine(ApplicationsRoot, "." + latest.Upload + "-" + package + ".part");
			string staging = Path.Combine(ApplicationsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
			try
			{
				await DownloadFromHostsAsync(relative, archive, displayName, report, cancellationToken).ConfigureAwait(false);
				report("Installing " + displayName);
				ExtractBundle(archive, staging, cancellationToken);
				string? bundle = Directory.EnumerateDirectories(staging, "*.app").FirstOrDefault();
				if (bundle == null || !File.Exists(Path.Combine(bundle, "Contents", "Info.plist")))
					throw new InvalidDataException("the package holds no application bundle");

				string? preserved = PreserveClientSettings(target, staging);
				if (Directory.Exists(target))
					Directory.Delete(target, true);
				Directory.Move(bundle, target);
				RestoreClientSettings(preserved, target);
				File.WriteAllText(target + ".version", stamp);
				return OperationResult<string>.Success(target);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				return installed && IsApplicationInstalled(application)
					? OperationResult<string>.Success(target)
					: OperationResult<string>.Fail("RobloxInstallFailed", "macOS " + displayName + " could not be installed: " + ex.Message);
			}
			finally
			{
				TryDelete(archive);
				TryDelete(staging);
			}
		}
		finally
		{
			RobloxGate.Release();
		}
	}

	public static ProcessStartInfo CreateLaunch(string runtime, RosemaryApplication application, string? launchUrl, IReadOnlyDictionary<string, string>? environment = null)
	{
		ProcessStartInfo info = new()
		{
			FileName = runtime,
			UseShellExecute = false,
			WorkingDirectory = Root
		};
		info.ArgumentList.Add("run");
		info.ArgumentList.Add(ApplicationPath(application));
		if (!string.IsNullOrWhiteSpace(launchUrl) && !IsPlaceholderLaunch(launchUrl))
		{
			info.ArgumentList.Add("-protocolString");
			info.ArgumentList.Add(launchUrl);
		}
		info.Environment["ROSEMARY_PREFIX"] = PrefixPath;
		info.Environment["ROSEMARY_PREFIX_SETUP"] = "download";
		info.Environment.Remove("LD_LIBRARY_PATH");
		info.Environment.Remove("LD_PRELOAD");
		if (environment != null)
		{
			foreach ((string name, string value) in environment)
			{
				if (name.StartsWith("ROSEMARY_", StringComparison.Ordinal) || name is "VK_DRIVER_FILES" or "SDL_VIDEO_DRIVER" or "VK_ADD_LAYER_PATH" or "VK_INSTANCE_LAYERS" or "VOIDSTRAP_OVERLAY_SHM")
					info.Environment[name] = value;
			}
		}
		return info;
	}

	public static bool IsPlaceholderLaunch(string url)
	{
		string trimmed = url.Trim().TrimEnd('/');
		return string.Equals(trimmed, "roblox://experiences/start", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(trimmed, "roblox-studio://launch", StringComparison.OrdinalIgnoreCase);
	}

	private static readonly object RunningCacheGate = new();
	private static long _runningCheckedAt = long.MinValue;
	private static bool _playerRunning;

	public static bool IsPlayerRunning
	{
		get
		{
			lock (RunningCacheGate)
			{
				long now = Environment.TickCount64;
				if (_runningCheckedAt == long.MinValue || now - _runningCheckedAt >= 1000)
				{
					_playerRunning = RunningProcessIds(RosemaryApplication.Player).Count > 0;
					_runningCheckedAt = now;
				}
				return _playerRunning;
			}
		}
	}

	public static async Task<bool> CloseAsync(RosemaryApplication application, CancellationToken cancellationToken = default)
	{
		IReadOnlyList<int> processIds = RunningProcessIds(application);
		if (processIds.Count == 0)
			return true;
		foreach (int processId in processIds)
			SendSignal(processId, 15);
		for (int attempt = 0; attempt < 30 && RunningProcessIds(application).Count > 0; attempt++)
			await Task.Delay(100, cancellationToken).ConfigureAwait(false);
		foreach (int processId in RunningProcessIds(application))
			SendSignal(processId, 9);
		for (int attempt = 0; attempt < 20 && RunningProcessIds(application).Count > 0; attempt++)
			await Task.Delay(100, cancellationToken).ConfigureAwait(false);
		lock (RunningCacheGate)
			_runningCheckedAt = long.MinValue;
		return RunningProcessIds(application).Count == 0;
	}

	private static void SendSignal(int processId, int signal)
	{
		try
		{
			_ = KillProcess(processId, signal);
		}
		catch (Exception)
		{
		}
	}

	[System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
	private static partial int KillProcess(int processId, int signal);

	public static long? ReadMemory(RosemaryApplication application)
	{
		IReadOnlyList<int> processIds = RunningProcessIds(application);
		if (processIds.Count == 0)
			return null;
		long total = 0;
		foreach (int processId in processIds)
		{
			string directory = "/proc/" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
			long value = ReadMemoryField(Path.Combine(directory, "smaps_rollup"), "Pss:");
			if (value <= 0)
				value = ReadMemoryField(Path.Combine(directory, "status"), "VmRSS:");
			total += Math.Max(0, value);
		}
		return total > 0 ? total : null;
	}

	private static long ReadMemoryField(string path, string field)
	{
		try
		{
			foreach (string line in File.ReadLines(path))
			{
				if (!line.StartsWith(field, StringComparison.Ordinal))
					continue;
				string[] parts = line[field.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
				return parts.Length > 0 && long.TryParse(parts[0], out long kilobytes) ? kilobytes * 1024 : 0;
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		return 0;
	}

	public static IReadOnlyList<int> RunningProcessIds(RosemaryApplication application)
	{
		List<int> found = [];
		string bundle = ApplicationPath(application);
		try
		{
			foreach (string directory in Directory.EnumerateDirectories("/proc"))
			{
				if (!int.TryParse(Path.GetFileName(directory), out int pid) || pid == Environment.ProcessId)
					continue;
				string commandLine;
				try
				{
					commandLine = File.ReadAllText(Path.Combine(directory, "cmdline"));
				}
				catch (IOException)
				{
					continue;
				}
				catch (UnauthorizedAccessException)
				{
					continue;
				}
				if (commandLine.Contains(bundle, StringComparison.Ordinal))
					found.Add(pid);
			}
		}
		catch (IOException)
		{
		}
		return found;
	}

	private static async Task<string> InstallRuntimeAsync(string version, string appImageUrl, string checksumsUrl, string appImageName, Action<string>? status, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(RuntimeRoot);
		string download = Path.Combine(RuntimeRoot, ".download-" + Guid.NewGuid().ToString("N"));
		string extract = Path.Combine(RuntimeRoot, ".extract-" + Guid.NewGuid().ToString("N"));
		string final = Path.Combine(RuntimeRoot, version);
		try
		{
			string checksums = await Http.GetStringAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
			string? expected = ParseChecksum(checksums, appImageName);
			if (expected == null)
				throw new InvalidDataException("the release has no checksum for " + appImageName);

			await DownloadFileAsync(appImageUrl, download, "Rosemary " + version, status, cancellationToken).ConfigureAwait(false);
			string actual;
			using (FileStream stream = File.OpenRead(download))
				actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
			if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("the downloaded AppImage does not match its published checksum");

			status?.Invoke("Installing Rosemary " + version);
			File.SetUnixFileMode(download, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			Directory.CreateDirectory(extract);
			ProcessStartInfo info = new()
			{
				FileName = download,
				UseShellExecute = false,
				WorkingDirectory = extract,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			info.ArgumentList.Add("--appimage-extract");
			using (Process process = Process.Start(info) ?? throw new InvalidOperationException("the AppImage could not be unpacked"))
			{
				Task drainOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
				Task drainError = process.StandardError.ReadToEndAsync(cancellationToken);
				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
				await Task.WhenAll(drainOut, drainError).ConfigureAwait(false);
				if (process.ExitCode != 0)
					throw new InvalidOperationException("the AppImage could not be unpacked, exit code " + process.ExitCode);
			}

			string unpacked = Path.Combine(extract, "squashfs-root");
			if (!File.Exists(Path.Combine(unpacked, "AppRun")))
				throw new InvalidDataException("the AppImage holds no AppRun");
			if (Directory.Exists(final))
				Directory.Delete(final, true);
			Directory.Move(unpacked, final);
			File.WriteAllText(Path.Combine(RuntimeRoot, VersionFileName), version);
			RemoveOldRuntimes(version);
			return Path.Combine(final, "AppRun");
		}
		finally
		{
			TryDelete(download);
			TryDelete(extract);
		}
	}

	private static void RemoveOldRuntimes(string keep)
	{
		try
		{
			foreach (string directory in Directory.EnumerateDirectories(RuntimeRoot))
			{
				string name = Path.GetFileName(directory);
				if (!string.Equals(name, keep, StringComparison.Ordinal))
					TryDelete(directory);
			}
		}
		catch (IOException)
		{
		}
	}

	private static async Task<(string Version, string AppImageUrl, string ChecksumsUrl, string AppImageName)> GetLatestReleaseAsync(CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseUrl);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		string tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
		string version = tag.TrimStart('v', 'V');
		if (!VersionPattern().IsMatch(version))
			throw new InvalidDataException("unexpected release tag " + tag);

		string appImageName = "Rosemary-" + version + "-x86_64.AppImage";
		string? appImageUrl = null;
		string? checksumsUrl = null;
		foreach (JsonElement asset in document.RootElement.GetProperty("assets").EnumerateArray())
		{
			string name = asset.GetProperty("name").GetString() ?? "";
			string url = asset.GetProperty("browser_download_url").GetString() ?? "";
			if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
				continue;
			if (name == appImageName)
				appImageUrl = url;
			else if (name == "SHA256SUMS")
				checksumsUrl = url;
		}
		if (appImageUrl == null || checksumsUrl == null)
			throw new InvalidDataException("release " + tag + " has no x86_64 AppImage with checksums");
		return (version, appImageUrl, checksumsUrl, appImageName);
	}

	private static string? ParseChecksum(string checksums, string fileName)
	{
		foreach (string line in checksums.Split('\n'))
		{
			Match match = ChecksumPattern().Match(line.Trim());
			if (match.Success && match.Groups[2].Value == fileName)
				return match.Groups[1].Value;
		}
		return null;
	}

	private static bool IsProduction(string? channel) =>
		string.IsNullOrWhiteSpace(channel) || string.Equals(channel.Trim(), "production", StringComparison.OrdinalIgnoreCase);

	private static async Task<(string Version, string Upload, string Channel)> GetLatestRobloxAsync(string binaryType, string? channel, CancellationToken cancellationToken)
	{
		if (!IsProduction(channel))
		{
			try
			{
				return await GetRobloxFromHostsAsync(binaryType, channel!.Trim(), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
			}
		}
		return await GetRobloxFromHostsAsync(binaryType, "production", cancellationToken).ConfigureAwait(false);
	}

	private static async Task<(string Version, string Upload, string Channel)> GetRobloxFromHostsAsync(string binaryType, string channel, CancellationToken cancellationToken)
	{
		string path = IsProduction(channel)
			? "/v2/client-version/" + binaryType
			: "/v2/client-version/" + binaryType + "/channel/" + Uri.EscapeDataString(channel);
		Exception? lastError = null;
		foreach (string host in ClientSettingsHosts)
		{
			try
			{
				using HttpResponseMessage response = await Http.GetAsync(host + path, cancellationToken).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
				string version = document.RootElement.GetProperty("version").GetString() ?? "";
				string upload = document.RootElement.GetProperty("clientVersionUpload").GetString() ?? "";
				if (!UploadPattern().IsMatch(upload))
					throw new InvalidDataException("Roblox returned an unexpected version: " + upload);
				return (version, upload, channel);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				lastError = ex;
			}
		}
		throw lastError ?? new HttpRequestException("Roblox version services are unavailable");
	}

	private static async Task DownloadFromHostsAsync(string relativePath, string destination, string displayName, Action<string>? status, CancellationToken cancellationToken)
	{
		Exception? lastError = null;
		foreach (string host in CdnHosts)
		{
			try
			{
				await DownloadFileAsync(host + "/" + relativePath, destination, displayName, status, cancellationToken).ConfigureAwait(false);
				return;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				lastError = ex;
				TryDelete(destination);
			}
		}
		throw new IOException(displayName + " could not be downloaded: " + lastError?.Message, lastError);
	}

	public static int DownloadSegments { get; set; } = 12;

	private const long SegmentThreshold = 8L << 20;
	private const long MinimumChunk = 4L << 20;
	private const int ChunkAttempts = 4;

	private static async Task DownloadFileAsync(string url, string destination, string displayName, Action<string>? status, CancellationToken cancellationToken)
	{
		int segments = Math.Clamp(DownloadSegments, 1, 16);
		(Uri location, long total, bool ranges) = await ProbeAsync(url, cancellationToken).ConfigureAwait(false);
		if (!ranges || segments == 1 || total < SegmentThreshold)
		{
			await DownloadSingleAsync(location, destination, displayName, status, cancellationToken).ConfigureAwait(false);
			return;
		}

		DownloadProgress progress = new(displayName, total, status);
		long chunk = Math.Max(MinimumChunk, total / (segments * 4));
		Queue<(long Start, long End)> pending = new();
		for (long offset = 0; offset < total; offset += chunk)
			pending.Enqueue((offset, Math.Min(total, offset + chunk) - 1));

		using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(destination, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, total);
		using CancellationTokenSource failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task[] workers = new Task[Math.Min(segments, pending.Count)];
		for (int index = 0; index < workers.Length; index++)
		{
			workers[index] = Task.Run(async () =>
			{
				byte[] buffer = new byte[1 << 20];
				while (true)
				{
					(long Start, long End) range;
					lock (pending)
					{
						if (pending.Count == 0)
							return;
						range = pending.Dequeue();
					}
					await DownloadRangeAsync(location, handle, range.Start, range.End, buffer, progress, failure.Token).ConfigureAwait(false);
				}
			}, failure.Token);
		}

		try
		{
			await Task.WhenAll(workers).ConfigureAwait(false);
		}
		catch
		{
			failure.Cancel();
			throw;
		}
		if (progress.Done != total)
			throw new IOException("the download stopped at " + progress.Done + " of " + total + " bytes");
	}

	private static async Task<(Uri Location, long Total, bool Ranges)> ProbeAsync(string url, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, url);
		request.Headers.Range = new RangeHeaderValue(0, 0);
		using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		Uri location = response.RequestMessage?.RequestUri ?? new Uri(url);
		if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is long length && length > 0)
			return (location, length, true);
		return (location, response.Content.Headers.ContentLength ?? 0, false);
	}

	private static async Task DownloadRangeAsync(Uri location, Microsoft.Win32.SafeHandles.SafeFileHandle handle, long start, long end, byte[] buffer, DownloadProgress progress, CancellationToken cancellationToken)
	{
		long position = start;
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				using HttpRequestMessage request = new(HttpMethod.Get, location);
				request.Headers.Range = new RangeHeaderValue(position, end);
				using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
				if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
					throw new IOException("the server ignored a range request");
				await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				int read;
				while (position <= end && (read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position + 1)), cancellationToken).ConfigureAwait(false)) > 0)
				{
					await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, cancellationToken).ConfigureAwait(false);
					position += read;
					progress.Add(read);
				}
				if (position > end)
					return;
				throw new IOException("a download segment ended early");
			}
			catch (Exception) when (attempt < ChunkAttempts && !cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private static async Task DownloadSingleAsync(Uri location, string destination, string displayName, Action<string>? status, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await Http.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		long? total = response.Content.Headers.ContentLength;
		DownloadProgress progress = new(displayName, total ?? 0, status);
		await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		await using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
		byte[] buffer = new byte[1 << 20];
		int read;
		while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
		{
			await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			progress.Add(read);
		}
		if (total.HasValue && progress.Done != total.Value)
			throw new IOException("the download stopped at " + progress.Done + " of " + total.Value + " bytes");
	}

	private sealed class DownloadProgress
	{
		private readonly string _name;
		private readonly long _total;
		private readonly Action<string>? _status;
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private long _done;
		private long _lastReport;

		public DownloadProgress(string name, long total, Action<string>? status)
		{
			_name = name;
			_total = total;
			_status = status;
		}

		public long Done => Interlocked.Read(ref _done);

		public void Add(int bytes)
		{
			long done = Interlocked.Add(ref _done, bytes);
			long now = _clock.ElapsedMilliseconds;
			long last = Interlocked.Read(ref _lastReport);
			if (_status == null || now - last < 250 && done < _total || Interlocked.CompareExchange(ref _lastReport, now, last) != last)
				return;
			double seconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
			string speed = (done / seconds / (1 << 20)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB/s";
			_status(_total > 0
				? "Downloading " + _name + " " + (int)(done * 100 / _total) + "% at " + speed
				: "Downloading " + _name + " at " + speed);
		}
	}

	private static readonly int ExtractWorkers = Math.Clamp(Environment.ProcessorCount, 1, 8);

	private static void ExtractBundle(string archivePath, string destination, CancellationToken cancellationToken)
	{
		string root = Path.GetFullPath(destination);
		Directory.CreateDirectory(root);
		List<(string Name, string Path, int Mode)> files = [];
		using (ZipArchive archive = ZipFile.OpenRead(archivePath))
		{
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string name = entry.FullName.Replace('\\', '/');
				if (name.Length == 0 || name.StartsWith('/') || name.Contains('\0') || name.Split('/').Contains(".."))
					throw new InvalidDataException("the package holds an unsafe path: " + name);
				if (name.StartsWith("__MACOSX/", StringComparison.Ordinal))
					continue;

				string path = Path.GetFullPath(Path.Combine(root, name));
				if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != root)
					throw new InvalidDataException("the package holds an unsafe path: " + name);

				int mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
				int kind = mode & 0xF000;
				if (name.EndsWith('/') || kind == 0x4000)
				{
					Directory.CreateDirectory(path);
					continue;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				if (kind == 0xA000)
				{
					string target;
					using (StreamReader reader = new(entry.Open()))
						target = reader.ReadToEnd();
					if (target.Length == 0 || target.StartsWith('/') || target.Contains('\0') || target.Split('/').Contains(".."))
						throw new InvalidDataException("the package holds an unsafe link: " + name);
					File.CreateSymbolicLink(path, target);
					continue;
				}

				files.Add((entry.FullName, path, mode));
			}
		}

		ParallelOptions options = new() { MaxDegreeOfParallelism = ExtractWorkers, CancellationToken = cancellationToken };
		int slice = (files.Count + ExtractWorkers - 1) / Math.Max(1, ExtractWorkers);
		Parallel.For(0, ExtractWorkers, options, worker =>
		{
			int first = worker * slice;
			int last = Math.Min(files.Count, first + slice);
			if (first >= last)
				return;
			using ZipArchive archive = ZipFile.OpenRead(archivePath);
			for (int index = first; index < last; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				(string name, string path, int mode) = files[index];
				ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException("the package lost an entry: " + name);
				entry.ExtractToFile(path, true);
				UnixFileMode permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
				if ((mode & 0x49) != 0)
					permissions |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
				File.SetUnixFileMode(path, permissions);
			}
		});
	}

	private static string? PreserveClientSettings(string bundle, string staging)
	{
		string source = Path.Combine(bundle, "Contents", "MacOS", "ClientSettings");
		if (!Directory.Exists(source))
			return null;
		string kept = Path.Combine(staging, ".ClientSettings");
		Directory.CreateDirectory(kept);
		foreach (string file in Directory.EnumerateFiles(source))
			File.Copy(file, Path.Combine(kept, Path.GetFileName(file)), true);
		return kept;
	}

	private static void RestoreClientSettings(string? kept, string bundle)
	{
		if (kept == null || !Directory.Exists(kept))
			return;
		string destination = Path.Combine(bundle, "Contents", "MacOS", "ClientSettings");
		Directory.CreateDirectory(destination);
		foreach (string file in Directory.EnumerateFiles(kept))
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
	}

	private static string? ReadText(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
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

	private static void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, true);
			else if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static HttpClient CreateClient()
	{
		SocketsHttpHandler handler = new()
		{
			MaxConnectionsPerServer = 32,
			EnableMultipleHttp2Connections = true,
			AutomaticDecompression = System.Net.DecompressionMethods.None,
			PooledConnectionLifetime = TimeSpan.FromMinutes(10),
			ConnectTimeout = TimeSpan.FromSeconds(15)
		};
		HttpClient client = new(handler) { Timeout = TimeSpan.FromMinutes(30) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
		return client;
	}

	[GeneratedRegex(@"^[0-9]+(\.[0-9]+)*$")]
	private static partial Regex VersionPattern();

	[GeneratedRegex(@"^version-[0-9a-f]+$")]
	private static partial Regex UploadPattern();

	[GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$")]
	private static partial Regex ChecksumPattern();
}
