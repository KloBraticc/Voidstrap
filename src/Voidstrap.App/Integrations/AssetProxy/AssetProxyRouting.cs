using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core.AssetProxy;

namespace Voidstrap.Integrations.AssetProxy;

internal static class AssetProxyRouting
{
	private const string Marker = "Voidstrap AssetWarp entry";

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
		if (!storage.StartsWith(roblox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("Roblox cache path validation failed");
		}
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
				throw new InvalidOperationException("Could not resolve a direct endpoint for " + host);
			}
			endpoints[host] = address;
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
	}

	public static bool Cleanup()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return true;
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
				return true;
			}
			catch (Exception ex)
			{
				failure = ex;
				if (attempt < 4)
				{
					Task.Delay(100).GetAwaiter().GetResult();
				}
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
		catch
		{
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
		catch
		{
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
			string escapedHosts = HostsPath.Replace("'", "''", StringComparison.Ordinal);
			string escapedMarker = Marker.Replace("'", "''", StringComparison.Ordinal);
			string script = "$p=" + SelfProcessId() + ";Wait-Process -Id $p -ErrorAction SilentlyContinue;$f='" + escapedHosts + "';if(Test-Path -LiteralPath $f){$l=Get-Content -LiteralPath $f|Where-Object{$_ -notmatch '" + escapedMarker + "'};$t=$f+'.voidstrap.guard.tmp';for($i=0;$i-lt5;$i++){try{[IO.File]::WriteAllLines($t,$l);[IO.File]::Replace($t,$f,$null);break}catch{Remove-Item -LiteralPath $t -Force -ErrorAction SilentlyContinue;Start-Sleep -Milliseconds 100}}};& ipconfig.exe /flushdns|Out-Null";
			string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-NonInteractive");
			startInfo.ArgumentList.Add("-WindowStyle");
			startInfo.ArgumentList.Add("Hidden");
			startInfo.ArgumentList.Add("-EncodedCommand");
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

	private static int SelfProcessId()
	{
		return Environment.ProcessId;
	}
}
