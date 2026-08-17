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

public static class ClassicHostRedirect
{
	private const string LOG_IDENT = "ClassicHostRedirect";

	private const string Marker = "# VOIDSTRAP-ORC";

	private const string Loopback = "127.0.0.1";

	private static readonly SemaphoreSlim MutationGate = new(1, 1);

	public static readonly string[] Domains = new string[]
	{
		"www.roblox.com",
		"roblox.com"
	};

	private static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

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

	public static string? Set(bool enable)
	{
		return SetAsync(enable, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
	}

	public static async Task<string?> SetAsync(bool enable, CancellationToken cancellationToken)
	{
		await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			bool needsWork = enable ? (!IsApplied() || !IsUrlAclReserved() || !ResolvesToLoopback()) : IsApplied();
			if (!needsWork)
			{
				return null;
			}
			if (ProcessElevation.IsAdministrator())
			{
				if (!enable)
					return await Task.Run(Remove, cancellationToken).ConfigureAwait(false)
						? null
						: "Voidstrap could not restore normal Roblox name resolution.";
				if (!await Task.Run(Apply, cancellationToken).ConfigureAwait(false))
					return "Voidstrap could not point the classic Roblox domains at the local server. Check that the Windows hosts file is writable.";
				return await WaitForLoopbackResolutionAsync(cancellationToken).ConfigureAwait(false);
			}
			return await RunElevatedAsync(enable, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			MutationGate.Release();
		}
	}

	public const string ServerPrefix = "http://+:80/";

	public static bool ResolvesToLoopback()
	{
		try
		{
			IPAddress[] addresses = Dns.GetHostAddresses(Domains[0]);
			return addresses.Length > 0 && addresses.All(IPAddress.IsLoopback);
		}
		catch
		{
			return false;
		}
	}

	private static async Task<string?> WaitForLoopbackResolutionAsync(CancellationToken cancellationToken)
	{
		for (int attempt = 0; attempt < 8; attempt++)
		{
			if (await Task.Run(ResolvesToLoopback, cancellationToken).ConfigureAwait(false))
			{
				App.Logger?.WriteLine(LOG_IDENT, Domains[0] + " now resolves to the local server");
				return null;
			}
			if (attempt == 0 || attempt == 3)
				await Task.Run(FlushDns, cancellationToken).ConfigureAwait(false);
			await Task.Delay(250, cancellationToken).ConfigureAwait(false);
		}
		App.Logger?.WriteLine(LOG_IDENT, Domains[0] + " still does not resolve to the local server after flushing the DNS cache");
		return "Windows is still resolving " + Domains[0] + " to the live Roblox site instead of the local server. The classic client would load the live site and fail. Try again in a moment, or run \"ipconfig /flushdns\" and retry.";
	}

	public static bool IsUrlAclReserved()
	{
		try
		{
			using Process? process = Process.Start(new ProcessStartInfo("netsh", "http show urlacl url=" + ServerPrefix)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			if (process == null)
			{
				return false;
			}
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(5000);
			return output.Contains(ServerPrefix, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static void EnsureUrlAcl()
	{
		try
		{
			if (IsUrlAclReserved())
			{
				return;
			}
			string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
			using Process? process = Process.Start(new ProcessStartInfo("netsh", "http add urlacl url=" + ServerPrefix + " user=\"" + user + "\"")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			process?.WaitForExit(8000);
			App.Logger?.WriteLine(LOG_IDENT, IsUrlAclReserved()
				? "Reserved " + ServerPrefix + " for " + user + ", the local ORC server can now bind port 80 without elevation"
				: "Could not reserve " + ServerPrefix + ", the local ORC server will fail to start");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "URL reservation failed: " + ex.Message);
		}
	}

	public static bool Apply()
	{
		try
		{
			EnsureUrlAcl();
			List<string> lines = ReadHostsWithoutMarker();
			if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
			{
				lines.Add(string.Empty);
			}
			string[] domains = Domains;
			foreach (string domain in domains)
			{
				lines.Add(Loopback + " " + domain + " " + Marker);
			}
			if (!WriteHosts(lines))
			{
				return false;
			}
			FlushDns();
			App.Logger?.WriteLine(LOG_IDENT, "Pointed " + Domains.Length + " classic Roblox domain(s) at the local ORC server");
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
			if (kept.Length != all.Length && WriteHosts(kept.ToList()))
			{
				FlushDns();
				App.Logger?.WriteLine(LOG_IDENT, "Restored normal Roblox name resolution");
			}
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Remove failed: " + ex.Message);
			return false;
		}
	}

	public static void RemoveIfStale()
	{
		RemoveStale(allowElevation: false);
	}

	public static void CleanStaleRedirect()
	{
		RemoveStale(allowElevation: true);
	}

	private static void RemoveStale(bool allowElevation)
	{
		try
		{
			if (!IsApplied())
			{
				return;
			}
			if (Voidstrap.Utility.ClassicClients.AnyClassicClientRunning())
			{
				App.Logger?.WriteLine(LOG_IDENT, "A classic client is still running, leaving the redirect in place");
				return;
			}
			if (ProcessElevation.IsAdministrator())
			{
				App.Logger?.WriteLine(LOG_IDENT, "A previous classic session left the redirect behind, clearing it");
				Remove();
				return;
			}
			if (!allowElevation)
			{
				App.Logger?.WriteLine(LOG_IDENT, "A previous classic session left the redirect behind, it will be cleared on the next Voidstrap start");
				return;
			}
			App.Logger?.WriteLine(LOG_IDENT, "A previous classic session left the redirect behind, asking for elevation to restore normal Roblox name resolution");
			string? failure = RunElevatedAsync(enable: false, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
			if (failure != null)
				App.Logger?.WriteLine(LOG_IDENT, "Stale cleanup was not completed: " + failure);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Stale cleanup failed: " + ex.Message);
		}
	}

	private static bool WriteHosts(List<string> lines)
	{
		string path = HostsPath;
		int existing = File.Exists(path)
			? File.ReadAllLines(path).Count((string l) => !l.Contains(Marker, StringComparison.Ordinal))
			: 0;
		int keeping = lines.Count((string l) => !l.Contains(Marker, StringComparison.Ordinal));
		if (existing > 0 && keeping < existing)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Refused to write the hosts file, it would have dropped " + (existing - keeping) + " unrelated line(s)");
			return false;
		}
		File.WriteAllLines(path, lines);
		return true;
	}

	private static List<string> ReadHostsWithoutMarker()
	{
		string hostsPath = HostsPath;
		if (!File.Exists(hostsPath))
		{
			return new List<string>();
		}
		return File.ReadAllLines(hostsPath).Where((string l) => !l.Contains(Marker, StringComparison.Ordinal)).ToList();
	}

	public static void RemoveWhenSessionEnds(int ownerProcessId)
	{
		Process? owner = null;
		try
		{
			owner = Process.GetProcessById(ownerProcessId);
		}
		catch
		{
		}

		try
		{
			App.Logger?.WriteLine(LOG_IDENT, "Holding the redirect until the classic session ends");

			bool started = false;
			for (int i = 0; i < 240; i++)
			{
				if (Voidstrap.Utility.ClassicClients.AnyClassicClientRunning())
				{
					started = true;
					break;
				}
				if (owner != null && owner.HasExited)
					break;
				Thread.Sleep(500);
			}

			if (started)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Classic client detected, the redirect stays until it closes");
				int idle = 0;
				while (idle < 6)
				{
					if (owner != null && owner.HasExited)
						break;
					idle = Voidstrap.Utility.ClassicClients.AnyClassicClientRunning() ? 0 : idle + 1;
					Thread.Sleep(500);
				}
			}
			else
			{
				App.Logger?.WriteLine(LOG_IDENT, "No classic client appeared, releasing the redirect");
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Session watch failed: " + ex.Message);
		}
		finally
		{
			try { owner?.Dispose(); } catch { }
		}

		Remove();
		App.Logger?.WriteLine(LOG_IDENT, "Classic session finished, normal Roblox name resolution is back");
	}

	private static async Task<string?> RunElevatedAsync(bool enable, CancellationToken cancellationToken)
	{
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrEmpty(processPath))
		{
			return "Voidstrap could not locate its own executable to request elevation.";
		}
		try
		{
			string argument = enable
				? "-orcredirect on:" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
				: "-orcredirect off";
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = processPath,
				Arguments = argument,
				UseShellExecute = true,
				Verb = "runas"
			});
			if (process == null)
			{
				return "Windows did not start the elevated Voidstrap helper.";
			}
			if (enable)
			{
				for (int i = 0; i < 60; i++)
				{
					if (IsApplied() && IsUrlAclReserved())
					{
						return await WaitForLoopbackResolutionAsync(cancellationToken).ConfigureAwait(false);
					}
					if (process.WaitForExit(250))
					{
						break;
					}
				}
				if (!IsApplied())
					return "Voidstrap could not point the classic Roblox domains at the local server. Check that the Windows hosts file is writable.";
				if (!IsUrlAclReserved())
					return "Voidstrap could not reserve port 80 for the local ORC server. Check that no other program owns " + ServerPrefix + ".";
				return await WaitForLoopbackResolutionAsync(cancellationToken).ConfigureAwait(false);
			}
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(20));
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Elevation was declined, the classic client cannot reach the local server without it");
			return "Voidstrap needs administrator rights to point the classic Roblox domains at the local server. The elevation prompt was declined.";
		}
		catch (OperationCanceledException)
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Elevated helper timed out");
			}
			return "The elevated Voidstrap helper did not finish in time.";
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Elevated helper failed: " + ex.Message);
			return "The elevated Voidstrap helper failed: " + ex.Message;
		}
		if (IsApplied() != enable)
		{
			return enable
				? "Voidstrap could not point the classic Roblox domains at the local server. Check that the Windows hosts file is writable."
				: "Voidstrap could not restore normal Roblox name resolution.";
		}
		return null;
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
