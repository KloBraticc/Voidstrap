using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

public static class TelemetryBlocker
{
	private const string LOG_IDENT = "TelemetryBlocker";

	private const string Marker = "# VOIDSTRAP-TELEMETRYBLOCK";
	private static readonly SemaphoreSlim MutationGate = new(1, 1);

	public static readonly string[] Domains = new string[]
	{
		"client-telemetry.roblox.com",
		"ephemeralcounters.api.roblox.com",
		"metrics.roblox.com",
		"tracing.roblox.com",
		"lms.roblox.com",
		"ncs.roblox.com",
		"gold.roblox.com",
		"abtesting.roblox.com",
		"upload.crashes.roblox.com",
		"upload.crashes.rbxinfra.com",
		"roblox.qq.com"
	};

	public static readonly string[] WebViewBlockedHosts = new string[]
	{
		"www.google-analytics.com",
		"ssl.google-analytics.com",
		"analytics.google.com",
		"stats.g.doubleclick.net",
		"bat.bing.com",
		"analytics.tiktok.com"
	};

	private static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

	public static void SyncSettingFromState()
	{
		if (App.LaunchSettings.TelemetryBlockFlag.Active)
		{
			return;
		}
		if (IsApplied() && !App.Settings.Prop.BlockRobloxTelemetry)
		{
			App.Settings.Prop.BlockRobloxTelemetry = true;
			App.Settings.SaveDeferred();
			App.Logger?.WriteLine(LOG_IDENT, "Setting reconciled to on to match the active hosts block");
		}
		RefreshStaleEntries();
	}

	public static string[] GetStaleBlockedDomains()
	{
		try
		{
			string hostsPath = HostsPath;
			if (!File.Exists(hostsPath))
			{
				return [];
			}
			HashSet<string> allowed = new(Domains, StringComparer.OrdinalIgnoreCase);
			HashSet<string> stale = new(StringComparer.OrdinalIgnoreCase);
			foreach (string line in File.ReadAllLines(hostsPath))
			{
				if (!line.Contains(Marker, StringComparison.Ordinal))
				{
					continue;
				}
				string entry = line.Substring(0, line.IndexOf(Marker, StringComparison.Ordinal));
				string[] parts = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 2 && !allowed.Contains(parts[1]))
				{
					stale.Add(parts[1]);
				}
			}
			return [.. stale];
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Could not inspect the hosts block: " + ex.Message);
			return [];
		}
	}

	private static void RefreshStaleEntries()
	{
		string[] stale = GetStaleBlockedDomains();
		if (stale.Length == 0)
		{
			return;
		}

		App.Logger?.WriteLine(LOG_IDENT, "The hosts block still contains " + stale.Length + " domain(s) that Voidstrap no longer blocks: " + string.Join(", ", stale));
		if (!ProcessElevation.IsAdministrator())
		{
			App.Logger?.WriteLine(LOG_IDENT, "Run Voidstrap as administrator once, or turn the telemetry block off and on, to clear them");
			return;
		}

		try
		{
			if (App.Settings.Prop.BlockRobloxTelemetry ? Apply() : Remove())
			{
				App.Logger?.WriteLine(LOG_IDENT, "Cleared the outdated hosts block entries");
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Could not clear the outdated hosts block entries: " + ex.Message);
		}
	}

	public static bool IsApplied()
	{
		try
		{
			string hostsPath = HostsPath;
			if (!File.Exists(hostsPath))
			{
				return false;
			}
			return File.ReadAllLines(hostsPath).Any((string l) => l.Contains(Marker, StringComparison.Ordinal));
		}
		catch
		{
			return false;
		}
	}

	public static bool Set(bool enable)
	{
		return SetAsync(enable, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
	}

	public static async Task<bool> SetAsync(bool enable, CancellationToken cancellationToken)
	{
		await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!enable && !IsApplied())
			{
				return true;
			}
			if (ProcessElevation.IsAdministrator())
			{
				return await Task.Run(() => enable ? Apply() : Remove(), cancellationToken).ConfigureAwait(false);
			}
			return await RunElevatedAsync(enable, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			MutationGate.Release();
		}
	}

	public static bool Apply()
	{
		try
		{
			List<string> lines = ReadHostsWithoutBlock();
			if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
			{
				lines.Add(string.Empty);
			}
			string[] domains = Domains;
			foreach (string domain in domains)
			{
				lines.Add("0.0.0.0 " + domain + " " + Marker);
				lines.Add(":: " + domain + " " + Marker);
			}
			File.WriteAllLines(HostsPath, lines);
			FlushDns();
			App.Logger?.WriteLine(LOG_IDENT, $"Blocked {Domains.Length} telemetry domains at the DNS level");
			Verify();
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Apply failed: " + ex.Message);
			return false;
		}
	}

	public static bool Remove()
	{
		try
		{
			string hostsPath = HostsPath;
			if (!File.Exists(hostsPath))
			{
				return true;
			}
			string[] all = File.ReadAllLines(hostsPath);
			string[] kept = all.Where((string l) => !l.Contains(Marker, StringComparison.Ordinal)).ToArray();
			if (kept.Length != all.Length)
			{
				File.WriteAllLines(hostsPath, kept);
				FlushDns();
				App.Logger?.WriteLine(LOG_IDENT, "Removed telemetry block entries");
			}
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Remove failed: " + ex.Message);
			return false;
		}
	}

	private static List<string> ReadHostsWithoutBlock()
	{
		string hostsPath = HostsPath;
		if (!File.Exists(hostsPath))
		{
			return new List<string>();
		}
		return File.ReadAllLines(hostsPath).Where((string l) => !l.Contains(Marker, StringComparison.Ordinal)).ToList();
	}

	private static async Task<bool> RunElevatedAsync(bool enable, CancellationToken cancellationToken)
	{
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrEmpty(processPath))
		{
			return false;
		}
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = processPath,
				Arguments = "-telemetryblock " + (enable ? "on" : "off"),
				UseShellExecute = true,
				Verb = "runas"
			});
			if (process == null)
			{
				return false;
			}
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(20));
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Elevation was declined by the user");
			return false;
		}
		catch (OperationCanceledException)
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Elevated helper timed out");
			}
			return false;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Elevated helper failed: " + ex.Message);
			return false;
		}
		return IsApplied() == enable;
	}

	private static void Verify()
	{
		try
		{
			IPAddress[] addrs = Dns.GetHostAddresses(Domains[0]);
			bool blackholed = addrs.Length == 0 || addrs.All((IPAddress a) => IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None));
			App.Logger?.WriteLine(LOG_IDENT, "Verification: " + Domains[0] + (blackholed ? " now resolves to a blackhole address" : " still resolves upstream, cached DNS may take a moment to clear"));
		}
		catch
		{
			App.Logger?.WriteLine(LOG_IDENT, "Verification: " + Domains[0] + " no longer resolves");
		}
	}

	private static void FlushDns()
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			process?.WaitForExit(3000);
		}
		catch
		{
		}
	}
}
