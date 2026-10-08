using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Voidstrap.Platform.MacOS;

public sealed record MacInstallOptions(
	int Segments = 12,
	int BufferBytes = 1 << 20,
	string? PreferredHost = null,
	string? Channel = null,
	bool ForceReinstall = false,
	bool UpdateRoblox = true,
	bool EarlyDelivery = false,
	Func<string, bool>? HoldUpdate = null);

public sealed partial class MacOSRobloxInstaller
{
	private const string ApplicationsDirectory = "/Applications";

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

	private static readonly string[] CdnHosts = ["https://setup.rbxcdn.com", "https://setup-aws.rbxcdn.com", "https://setup-ak.rbxcdn.com", "https://s3.amazonaws.com/setup.roblox.com"];

	private static readonly string[] ClientSettingsHosts = ["https://clientsettingscdn.roblox.com", "https://clientsettings.roblox.com"];

	private readonly IProcessService _processes;

	public MacOSRobloxInstaller(IProcessService processes)
	{
		_processes = processes;
	}

	public async Task<OperationResult<string>> EnsureLatestAsync(RuntimeKind kind, string? installedPath, Action<string>? status, MacInstallOptions? options = null, CancellationToken cancellationToken = default)
	{
		options ??= new MacInstallOptions();
		bool player = kind == RuntimeKind.Player;
		string channel = player ? "MacPlayer" : "MacStudio";
		string package = player ? "RobloxPlayer.zip" : "RobloxStudioApp.zip";
		string applicationName = player ? "Roblox.app" : "RobloxStudio.app";
		string displayName = player ? "Roblox" : "Roblox Studio";
		bool installed = !string.IsNullOrWhiteSpace(installedPath) && Directory.Exists(installedPath);
		if (installed && !options.ForceReinstall && !options.UpdateRoblox)
		{
			return OperationResult<string>.Success(installedPath!);
		}

		status?.Invoke("Checking for " + displayName + " updates");
		(string Version, string Upload, string Channel) latest;
		try
		{
			latest = await GetLatestVersionAsync(channel, options.Channel, options.EarlyDelivery, cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return installed
				? OperationResult<string>.Success(installedPath!)
				: OperationResult<string>.Fail("RobloxVersionUnavailable", "The latest " + displayName + " version could not be checked: " + ex.Message);
		}

		if (installed && !options.ForceReinstall)
		{
			string? current = await ReadBundleVersionAsync(installedPath!, cancellationToken);
			if (string.Equals(current, latest.Version, StringComparison.Ordinal))
			{
				return OperationResult<string>.Success(installedPath!);
			}
			if (!string.IsNullOrEmpty(current) && options.HoldUpdate?.Invoke(latest.Version) == true)
			{
				return OperationResult<string>.Success(installedPath!);
			}
		}

		string target = installed ? installedPath! : Path.Combine(ChooseApplicationsDirectory(), applicationName);
		string work = Path.Combine(Path.GetTempPath(), "voidstrap-roblox-" + Guid.NewGuid().ToString("N"));
		string staging = Path.Combine(Path.GetDirectoryName(target)!, "." + applicationName + ".installing-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(work);
			string archive = Path.Combine(work, package);
			string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "mac/arm64/" : "mac/";
			string prefix = IsProduction(latest.Channel) ? "" : "channel/" + latest.Channel.ToLowerInvariant() + "/";
			await DownloadAsync(prefix + architecture + latest.Upload + "-" + package, archive, displayName, status, Math.Clamp(options.Segments, 1, 16), Math.Clamp(options.BufferBytes, 16 * 1024, 16 << 20), options.PreferredHost, cancellationToken);

			status?.Invoke("Installing " + displayName);
			string extracted = Path.Combine(work, "extracted");
			await RunAsync("/usr/bin/ditto", ["-x", "-k", archive, extracted], cancellationToken);
			string? bundle = Directory.GetDirectories(extracted, "*.app").FirstOrDefault();
			if (bundle == null)
			{
				return OperationResult<string>.Fail("RobloxPackageInvalid", "The downloaded " + displayName + " package did not contain an application");
			}

			await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", bundle], cancellationToken);
			await RunAsync("/usr/bin/ditto", [bundle, staging], cancellationToken);
			if (Directory.Exists(target))
			{
				Directory.Delete(target, true);
			}
			Directory.Move(staging, target);
			return OperationResult<string>.Success(target);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return installed && Directory.Exists(installedPath)
				? OperationResult<string>.Success(installedPath!)
				: OperationResult<string>.Fail("RobloxInstallFailed", displayName + " could not be installed: " + ex.Message);
		}
		finally
		{
			TryDelete(staging);
			TryDelete(work);
		}
	}

	public static void WriteClientSettings(string applicationPath, string? sourceFile)
	{
		string directory = Path.Combine(applicationPath, "Contents", "MacOS", "ClientSettings");
		string destination = Path.Combine(directory, "ClientAppSettings.json");
		if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
		{
			if (File.Exists(destination))
			{
				File.Delete(destination);
			}
			return;
		}

		Directory.CreateDirectory(directory);
		File.Copy(sourceFile, destination, true);
	}

	private static bool IsProduction(string? channel) => string.IsNullOrWhiteSpace(channel) || string.Equals(channel.Trim(), "production", StringComparison.OrdinalIgnoreCase);

	private static async Task<(string Version, string Upload, string Channel)> GetLatestVersionAsync(string binaryType, string? channel, bool early, CancellationToken cancellationToken)
	{
		if (!IsProduction(channel))
		{
			try
			{
				return await GetVersionFromHostsAsync(binaryType, channel!.Trim(), early, cancellationToken);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
			}
		}
		return await GetVersionFromHostsAsync(binaryType, "production", early, cancellationToken);
	}

	private static async Task<(string Version, string Upload, string Channel)> GetVersionFromHostsAsync(string binaryType, string channel, bool early, CancellationToken cancellationToken)
	{
		string path = IsProduction(channel) ? "/v2/client-version/" + binaryType : "/v2/client-version/" + binaryType + "/channel/" + Uri.EscapeDataString(channel);
		Exception? lastError = null;
		foreach (string host in early ? Enumerable.Reverse(ClientSettingsHosts) : ClientSettingsHosts)
		{
			try
			{
				using HttpResponseMessage response = await Http.GetAsync(host + path, cancellationToken);
				response.EnsureSuccessStatusCode();
				using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
				string version = document.RootElement.GetProperty("version").GetString() ?? "";
				string upload = document.RootElement.GetProperty("clientVersionUpload").GetString() ?? "";
				if (!UploadPattern().IsMatch(upload))
				{
					throw new InvalidDataException("Roblox returned an unexpected version: " + upload);
				}
				return (version, upload, channel);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				lastError = ex;
			}
		}
		throw lastError ?? new HttpRequestException("Roblox version services are unavailable");
	}

	private static async Task DownloadAsync(string relativePath, string destination, string displayName, Action<string>? status, int segments, int bufferBytes, string? preferredHost, CancellationToken cancellationToken)
	{
		Exception? lastError = null;
		string preferred = preferredHost?.Trim().TrimEnd('/') ?? "";
		IEnumerable<string> hosts = CdnHosts.OrderBy(host => string.Equals(host, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
		foreach (string host in hosts)
		{
			try
			{
				await DownloadFileAsync(host + "/" + relativePath, destination, displayName, status, segments, bufferBytes, cancellationToken).ConfigureAwait(false);
				return;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				lastError = ex;
			}
		}
		throw new IOException(displayName + " could not be downloaded: " + lastError?.Message, lastError);
	}

	private static async Task DownloadFileAsync(string url, string destination, string displayName, Action<string>? status, int segments, int bufferBytes, CancellationToken cancellationToken)
	{
		if (segments > 1)
		{
			try
			{
				using HttpRequestMessage request = new(HttpMethod.Head, url);
				using HttpResponseMessage head = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
				if (head.IsSuccessStatusCode && head.Content.Headers.ContentLength is long size && size >= 1 << 20 && head.Headers.AcceptRanges.Contains("bytes"))
				{
					await DownloadSegmentsAsync(url, destination, displayName, status, segments, bufferBytes, size, head.Headers.ETag, cancellationToken).ConfigureAwait(false);
					return;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
			}
		}

		using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		long? total = response.Content.Headers.ContentLength;
		await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		await using FileStream target = File.Create(destination);
		byte[] buffer = new byte[bufferBytes];
		long received = 0;
		int lastPercent = -1;
		int read;
		while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
		{
			await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			received += read;
			int percent = total > 0 ? (int)(received * 100 / total.Value) : -1;
			if (percent != lastPercent)
			{
				lastPercent = percent;
				status?.Invoke(percent >= 0 ? $"Downloading {displayName} ({percent}%)" : "Downloading " + displayName);
			}
		}
		if (total > 0 && received != total)
		{
			throw new IOException("The download ended early");
		}
	}

	private static async Task DownloadSegmentsAsync(string url, string destination, string displayName, Action<string>? status, int segments, int bufferBytes, long total, EntityTagHeaderValue? etag, CancellationToken cancellationToken)
	{
		await using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
		target.SetLength(total);
		long received = 0;
		int lastPercent = -1;
		object progress = new();
		await Parallel.ForAsync(0, segments, new ParallelOptions { MaxDegreeOfParallelism = segments, TaskScheduler = TaskScheduler.Default, CancellationToken = cancellationToken }, async (segment, token) =>
		{
			long start = total * segment / segments;
			long end = total * (segment + 1) / segments - 1;
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			request.Headers.Range = new RangeHeaderValue(start, end);
			if (etag is { IsWeak: false })
				request.Headers.IfRange = new RangeConditionHeaderValue(etag);
			using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
			if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || range?.From != start || range.To != end || range.Length != total || (etag is not null && !etag.Equals(response.Headers.ETag)))
				throw new IOException("The server did not return the requested download segment");
			await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			byte[] buffer = new byte[bufferBytes];
			long offset = start;
			int read;
			while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
			{
				if (read > end - offset + 1)
					throw new IOException("The download segment exceeded its expected size");
				await RandomAccess.WriteAsync(target.SafeFileHandle, buffer.AsMemory(0, read), offset, token).ConfigureAwait(false);
				offset += read;
				lock (progress)
				{
					received += read;
					int percent = (int)(received * 100 / total);
					if (percent != lastPercent)
					{
						lastPercent = percent;
						status?.Invoke($"Downloading {displayName} ({percent}%)");
					}
				}
			}
			if (offset != end + 1)
				throw new IOException("The download segment ended early");
		}).ConfigureAwait(false);
	}

	public async Task<string?> ReadBundleVersionAsync(string applicationPath, CancellationToken cancellationToken)
	{
		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(
			new ProcessCommand("/usr/libexec/PlistBuddy", ["-c", "Print :CFBundleShortVersionString", Path.Combine(applicationPath, "Contents", "Info.plist")]),
			cancellationToken);
		return result.Succeeded && result.Value is { ExitCode: 0 } execution ? execution.StandardOutput.Trim() : null;
	}

	private async Task RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(new ProcessCommand(fileName, arguments), cancellationToken);
		if (!result.Succeeded || result.Value is null)
		{
			throw new InvalidOperationException(Path.GetFileName(fileName) + " could not run: " + result.Failure?.Message);
		}
		if (result.Value.ExitCode != 0)
		{
			throw new InvalidOperationException(Path.GetFileName(fileName) + " failed: " + result.Value.StandardError.Trim());
		}
	}

	private static string ChooseApplicationsDirectory()
	{
		try
		{
			string probe = Path.Combine(ApplicationsDirectory, ".voidstrap-write-" + Guid.NewGuid().ToString("N"));
			File.WriteAllBytes(probe, []);
			File.Delete(probe);
			return ApplicationsDirectory;
		}
		catch (Exception)
		{
			string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
			Directory.CreateDirectory(user);
			return user;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}
		}
		catch (Exception)
		{
		}
	}

	[GeneratedRegex("^version-[0-9a-f]{16}$")]
	private static partial Regex UploadPattern();
}
