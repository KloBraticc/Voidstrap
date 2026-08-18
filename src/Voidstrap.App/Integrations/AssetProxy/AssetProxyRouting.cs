using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core.AssetProxy;

namespace Voidstrap.Integrations.AssetProxy;

internal static class AssetProxyRouting
{
	private const string Marker = "Voidstrap AssetWarp entry";

	private const string RecoveryTaskName = "Voidstrap\\AssetWarpCleanup";

	private sealed class CleanupGuardPayload
	{
		public int ProcessId { get; set; }

		public long ProcessStartTicks { get; set; }
	}

	private const string LegacyPresenceMarker = "# VOIDSTRAP-PRESENCEOFF";

	private static readonly string HostsPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.System),
		"drivers",
		"etc",
		"hosts");

	private static readonly System.Threading.Lock Gate = new();

	private static readonly System.Threading.Lock GuardGate = new();

	private static string[] _activeHosts = [];

	private static Process? _cleanupGuard;

	public static void ClearRobloxCache()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string roblox = Path.GetFullPath(Path.Combine(local, "Roblox"));
		string gdk = Path.GetFullPath(Path.Combine(local, "RobloxPCGDK"));
		string[] files =
		[
			Path.Combine(roblox, "rbx-storage.db"),
			Path.Combine(gdk, "rbx-storage.db")
		];
		foreach (string file in files)
		{
			try
			{
				if (File.Exists(file))
				{
					File.SetAttributes(file, FileAttributes.Normal);
					File.Delete(file);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine("AssetProxyRouting", "Roblox cache file is locked: " + Path.GetFileName(file));
			}
		}

		string storage = Path.GetFullPath(Path.Combine(roblox, "rbx-storage"));
		if (Directory.Exists(storage))
		{
			foreach (string file in Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories))
			{
				try
				{
					File.SetAttributes(file, FileAttributes.Normal);
					File.Delete(file);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
			try
			{
				Directory.Delete(storage, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
		App.Logger?.WriteLine("AssetProxyRouting", "Roblox asset cache cleared");
	}

	public static async Task<IReadOnlyDictionary<string, string>> PrepareAsync(IEnumerable<string> hosts, CancellationToken ct, bool resolveEndpoints = true)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string[] requested = [.. hosts
			.Where(host => !string.IsNullOrWhiteSpace(host))
			.Select(host => host.Trim().ToLowerInvariant())
			.Distinct(StringComparer.OrdinalIgnoreCase)];

		if (RemoveEntries(requested))
		{
			FlushDns();
		}
		if (!resolveEndpoints)
		{
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}

		Task<(string Host, string? Address)>[] lookups = [.. requested.Select(async host =>
		{
			ct.ThrowIfCancellationRequested();
			string? address = await DnsResolver.ResolveDirectAsync(host, ct).ConfigureAwait(false);
			return (host, address);
		})];
		(string Host, string? Address)[] resolved = await Task.WhenAll(lookups).ConfigureAwait(false);
		Dictionary<string, string> endpoints = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string host, string? address) in resolved)
		{
			if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out IPAddress? parsed) || IPAddress.IsLoopback(parsed))
			{
				App.Logger?.WriteLine("AssetProxyRouting", "No direct endpoint for " + host + " yet, it will be resolved on demand");
				continue;
			}
			endpoints[host] = address;
		}

		if (endpoints.Count == 0)
		{
			throw new InvalidOperationException("Could not resolve a direct endpoint for any AssetWarp host");
		}

		return endpoints;
	}

	public static void InstallEntries(IEnumerable<string> hosts, bool includeIpv6)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;
		string[] requested = [.. hosts
			.Where(host => !string.IsNullOrWhiteSpace(host))
			.Select(host => host.Trim().ToLowerInvariant())
			.Distinct(StringComparer.OrdinalIgnoreCase)];

		lock (Gate)
		{
			List<string> lines = ReadLines();
			lines = RemoveOwnedAndLegacyLines(lines, requested);
			foreach (string host in requested)
			{
				lines.Add("127.0.0.1 " + host + " # " + Marker);
				if (includeIpv6)
				{
					lines.Add("::1 " + host + " # " + Marker);
				}
			}
			WriteLines(lines);
			_activeHosts = requested;
		}

		FlushDns();
		VerifyEntries(requested, includeIpv6);
		StartCleanupGuard();
		ArmRecoveryTask();
	}

	public static bool Cleanup(TimeSpan? budget = null)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return true;
		long deadline = Environment.TickCount64 + Math.Max(500L, (long)(budget ?? TimeSpan.FromMilliseconds(600)).TotalMilliseconds);
		string[] hosts;
		lock (Gate)
		{
			hosts = _activeHosts;
			_activeHosts = [];
		}

		Exception? failure = null;
		for (int attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				if (RemoveEntries(hosts))
				{
					FlushDns();
				}
				StopCleanupGuard();
				DisarmRecoveryTask();
				return true;
			}
			catch (Exception ex)
			{
				failure = ex;
				if (attempt >= 4 || Environment.TickCount64 + 100 >= deadline)
				{
					break;
				}
				Task.Delay(100).GetAwaiter().GetResult();
			}
		}
		App.Logger?.WriteLine("AssetProxyRouting", "Hosts cleanup failed: " + failure?.Message);
		return false;
	}

	public static bool HasInstalledEntries()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;
		try
		{
			return ReadLines().Any(line =>
				line.Contains(Marker, StringComparison.OrdinalIgnoreCase) ||
				line.Contains(LegacyPresenceMarker, StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return false;
		}
	}

	private static bool RemoveEntries(IEnumerable<string> hosts)
	{
		string[] requested = [.. hosts];
		lock (Gate)
		{
			List<string> lines = ReadLines();
			List<string> filtered = RemoveOwnedAndLegacyLines(lines, requested);
			if (filtered.Count != lines.Count)
			{
				WriteLines(filtered);
				return true;
			}
			return false;
		}
	}

	private static List<string> RemoveOwnedAndLegacyLines(IEnumerable<string> lines, IReadOnlyCollection<string> hosts)
	{
		return [.. lines.Where(line =>
		{
			if (line.Contains(LegacyPresenceMarker, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (line.Contains(Marker, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (!line.Contains("#gu_acc", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return !hosts.Any(host => ContainsHost(line, host));
		})];
	}

	private static bool ContainsHost(string line, string host)
	{
		string content = line.Split('#', 2)[0];
		return content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Skip(1)
			.Any(value => value.Equals(host, StringComparison.OrdinalIgnoreCase));
	}

	private static List<string> ReadLines()
	{
		if (!File.Exists(HostsPath))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(HostsPath)!);
			return [];
		}
		return [.. File.ReadAllLines(HostsPath)];
	}

	private static void WriteLines(IEnumerable<string> lines)
	{
		string directory = Path.GetDirectoryName(HostsPath)!;
		Directory.CreateDirectory(directory);
		string temporary = Path.Combine(directory, "hosts.voidstrap.tmp");
		HostsFileWriter.WriteAllLines(HostsPath, lines, temporary);
	}

	private static void VerifyEntries(IEnumerable<string> hosts, bool includeIpv6)
	{
		string[] lines = File.ReadAllLines(HostsPath);
		foreach (string host in hosts)
		{
			bool ipv4 = lines.Any(line => line.Contains(Marker, StringComparison.OrdinalIgnoreCase) && line.TrimStart().StartsWith("127.0.0.1 ", StringComparison.Ordinal) && ContainsHost(line, host));
			bool ipv6 = !includeIpv6 || lines.Any(line => line.Contains(Marker, StringComparison.OrdinalIgnoreCase) && line.TrimStart().StartsWith("::1 ", StringComparison.Ordinal) && ContainsHost(line, host));
			if (!ipv4 || !ipv6)
			{
				throw new IOException("Could not verify AssetWarp routing for " + host);
			}
		}
	}

	private static void FlushDns()
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"),
				Arguments = "/flushdns",
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			process?.WaitForExit(5000);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetProxyRouting", "The DNS cache could not be flushed, routing changes may be delayed: " + ex.Message);
		}
	}

	private static void StopCleanupGuard()
	{
		Process? guard;
		lock (GuardGate)
		{
			guard = _cleanupGuard;
			_cleanupGuard = null;
		}
		if (guard == null)
		{
			return;
		}
		try
		{
			if (!guard.HasExited)
			{
				guard.Kill();
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetProxyRouting", "The AssetWarp cleanup guard could not be stopped: " + ex.Message);
		}
		try
		{
			guard.Dispose();
		}
		catch
		{
		}
	}

	private static void StartCleanupGuard()
	{
		try
		{
			StopCleanupGuard();
			string executable = Paths.Process;
			if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
			{
				App.Logger?.WriteLine("AssetProxyRouting", "Cleanup guard skipped, the Voidstrap executable path is unavailable");
				return;
			}
			using Process current = Process.GetCurrentProcess();
			CleanupGuardPayload payload = new()
			{
				ProcessId = current.Id,
				ProcessStartTicks = current.StartTime.ToUniversalTime().Ticks
			};
			string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
			ProcessStartInfo startInfo = new()
			{
				FileName = executable,
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			startInfo.ArgumentList.Add("-assetwarpguard");
			startInfo.ArgumentList.Add(encoded);
			Process? started = Process.Start(startInfo);
			lock (GuardGate)
			{
				_cleanupGuard = started;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetProxyRouting", "Cleanup guard failed: " + ex.Message);
		}
	}

	public static void RunScheduledCleanup()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		try
		{
			if (AnyOtherVoidstrapRunning())
			{
				return;
			}
			if (RemoveEntries([]))
			{
				FlushDns();
			}
			DisarmRecoveryTask();
		}
		catch
		{
		}
	}

	private static bool AnyOtherVoidstrapRunning()
	{
		int self = Environment.ProcessId;
		Process[] found = Process.GetProcessesByName("Voidstrap");
		try
		{
			return found.Any(process => process.Id != self);
		}
		finally
		{
			foreach (Process process in found)
			{
				try
				{
					process.Dispose();
				}
				catch
				{
				}
			}
		}
	}

	private static bool RunScheduler(string[] arguments, int timeoutMilliseconds = 8000)
	{
		try
		{
			ProcessStartInfo startInfo = new()
			{
				FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (string argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}
			using Process? process = Process.Start(startInfo);
			if (process == null)
			{
				return false;
			}
			if (!process.WaitForExit(timeoutMilliseconds))
			{
				return false;
			}
			return process.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}

	private static void ArmRecoveryTask()
	{
		try
		{
			string executable = Paths.Process;
			if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
			{
				return;
			}
			bool created = RunScheduler([
				"/Create",
				"/TN", RecoveryTaskName,
				"/TR", "\"" + executable + "\" -assetwarpcleanup",
				"/SC", "ONLOGON",
				"/RL", "HIGHEST",
				"/F"
			]);
			App.Logger?.WriteLine("AssetProxyRouting", created
				? "Armed the AssetWarp recovery task, leftover routing will clear itself without another administrator prompt"
				: "Could not arm the AssetWarp recovery task, leftover routing will need administrator access to clear");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AssetProxyRouting", "Arming the AssetWarp recovery task failed: " + ex.Message);
		}
	}

	private static void DisarmRecoveryTask()
	{
		RunScheduler(["/Delete", "/TN", RecoveryTaskName, "/F"], 5000);
	}

	public static bool TryRunRecoveryTask(bool waitForCompletion)
	{
		if (!Voidstrap.Utility.Platform.IsWindows || !HasInstalledEntries())
		{
			return true;
		}
		if (!RunScheduler(["/Run", "/TN", RecoveryTaskName], 5000))
		{
			App.Logger?.WriteLine("AssetProxyRouting", "The AssetWarp recovery task is not available, leftover routing needs administrator access to clear");
			return false;
		}
		if (!waitForCompletion)
		{
			App.Logger?.WriteLine("AssetProxyRouting", "Started the AssetWarp recovery task to clear leftover routing in the background");
			return false;
		}
		for (int attempt = 0; attempt < 12; attempt++)
		{
			Task.Delay(150).GetAwaiter().GetResult();
			if (!HasInstalledEntries())
			{
				FlushDns();
				App.Logger?.WriteLine("AssetProxyRouting", "The AssetWarp recovery task cleared the leftover routing");
				return true;
			}
		}
		return false;
	}

	public static async Task RunCleanupGuardAsync(string? encodedPayload)
	{
		if (!Voidstrap.Utility.Platform.IsWindows || string.IsNullOrWhiteSpace(encodedPayload) || encodedPayload.Length > 4096)
		{
			return;
		}

		CleanupGuardPayload? payload;
		try
		{
			byte[] serialized = Convert.FromBase64String(encodedPayload);
			if (serialized.Length > 2048)
			{
				return;
			}
			payload = JsonSerializer.Deserialize<CleanupGuardPayload>(serialized);
		}
		catch
		{
			return;
		}

		if (payload == null || payload.ProcessId <= 0 || payload.ProcessStartTicks <= 0)
		{
			return;
		}

		try
		{
			using Process parent = Process.GetProcessById(payload.ProcessId);
			if (parent.StartTime.ToUniversalTime().Ticks != payload.ProcessStartTicks)
			{
				return;
			}
			await parent.WaitForExitAsync().ConfigureAwait(false);
		}
		catch (ArgumentException)
		{
		}
		catch (InvalidOperationException)
		{
		}
		catch
		{
			return;
		}

		for (int attempt = 0; attempt < 6; attempt++)
		{
			try
			{
				if (RemoveEntries([]))
				{
					FlushDns();
				}
				return;
			}
			catch when (attempt < 5)
			{
				await Task.Delay(250).ConfigureAwait(false);
			}
			catch
			{
				return;
			}
		}
	}

}
