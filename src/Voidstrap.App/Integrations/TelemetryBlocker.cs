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
		"ecsv2.roblox.com",
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
		"dfw2-128-116-95-3.roblox.com",
		"ams2-128-116-21-3.roblox.com",
		"atl1-128-116-99-3.roblox.com",
		"lax2-128-116-116-3.roblox.com",
		"nrt1-128-116-120-3.roblox.com",
		"ord2-128-116-101-3.roblox.com",
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
