using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Voidstrap.Core.AssetProxy;

namespace Voidstrap.Integrations.AssetProxy;

internal static partial class AssetWarpLegacyCleanup
{
	private const string LogIdent = "AssetWarpLegacyCleanup";

	private const string HostsMarker = "Voidstrap AssetWarp entry";

	private const string LegacyPresenceMarker = "# VOIDSTRAP-PRESENCEOFF";

	private const string RecoveryTaskName = "Voidstrap\\AssetWarpCleanup";

	private const string CaSubject = "CN=Voidstrap Proxy CA";

	private static readonly string HostsPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.System),
		"drivers",
		"etc",
		"hosts");

	private static readonly System.Threading.Lock Gate = new();

	[GeneratedRegex("-----BEGIN CERTIFICATE-----[\\s\\S]*?-----END CERTIFICATE-----", RegexOptions.CultureInvariant)]
	private static partial Regex CertificatePattern();

	private static string SoberDataRoot => Path.Combine(
		Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".var", "app", "org.vinegarhq.Sober", "data", "sober");

	public static void Run()
	{
		lock (Gate)
		{
			if (Voidstrap.Utility.Platform.IsLinux)
				Voidstrap.Platform.Linux.LinuxSoberProxyCleanup.RemoveLegacyOverride();
			if (Voidstrap.Utility.Platform.IsWindows && Voidstrap.Utility.ProcessElevation.IsAdministrator())
				RemoveHostsEntries();
			if (!Directory.Exists(Paths.AssetProxy))
				return;

			FleasionBridge.Restore();
			if (FleasionBridge.HasPendingRestore)
			{
				App.Logger?.WriteLine(LogIdent, "Fleasion routing is still pending restore, AssetWarp data is kept until the next start");
				return;
			}
			if (Voidstrap.Utility.Platform.IsLinux)
				RestoreSoberTrust();
			RemoveCertificates();
			if (Voidstrap.Utility.Platform.IsWindows)
				RunScheduler(["/Delete", "/TN", RecoveryTaskName, "/F"]);
			try
			{
				Directory.Delete(Paths.AssetProxy, true);
				App.Logger?.WriteLine(LogIdent, "Removed the leftover AssetWarp data");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "Leftover AssetWarp data could not be removed: " + ex.Message);
			}
		}
	}

	public static bool HasHostsEntries()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;
		try
		{
			return File.Exists(HostsPath) && File.ReadLines(HostsPath).Any(IsOwnedLine);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	public static void RemoveHostsEntries()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;
		try
		{
			if (!File.Exists(HostsPath))
				return;
			string[] lines = File.ReadAllLines(HostsPath);
			string[] kept = [.. lines.Where(line => !IsOwnedLine(line))];
			if (kept.Length == lines.Length)
				return;
			HostsFileWriter.WriteAllLines(HostsPath, kept, Path.Combine(Path.GetDirectoryName(HostsPath)!, "hosts.voidstrap.tmp"));
			FlushDns();
			App.Logger?.WriteLine(LogIdent, "Removed leftover AssetWarp routing from the hosts file");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "Leftover AssetWarp routing could not be removed from the hosts file: " + ex.Message);
		}
	}

	private static bool IsOwnedLine(string line)
	{
		return line.Contains(HostsMarker, StringComparison.OrdinalIgnoreCase)
			|| line.Contains(LegacyPresenceMarker, StringComparison.OrdinalIgnoreCase);
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
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			App.Logger?.WriteLine(LogIdent, "The DNS cache could not be flushed: " + ex.Message);
		}
	}

	private static void RunScheduler(string[] arguments)
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
				startInfo.ArgumentList.Add(argument);
			using Process? process = Process.Start(startInfo);
			process?.WaitForExit(5000);
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			App.Logger?.WriteLine(LogIdent, "The AssetWarp recovery task could not be removed: " + ex.Message);
		}
	}

	private static void RestoreSoberTrust()
	{
		string trustPath = Path.Combine(SoberDataRoot, "exe", "ssl", "cacert.pem");
		string certificates = Path.Combine(Paths.AssetProxy, "Certificates");
		string backupPath = Path.Combine(certificates, "sober-cacert.backup");
		string statePath = Path.Combine(certificates, "sober-cacert.state");
		try
		{
			string state = File.Exists(statePath) ? File.ReadAllText(statePath).Trim() : "";
			if (state.Equals("restore", StringComparison.Ordinal) && File.Exists(backupPath))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(trustPath)!);
				File.Move(backupPath, trustPath, true);
			}
			else if (state.Equals("delete", StringComparison.Ordinal) && File.Exists(trustPath))
			{
				File.Delete(trustPath);
			}
			foreach (string legacy in new[]
			{
				trustPath + ".voidstrap.tmp",
				Path.Combine(SoberDataRoot, "asset_overlay", "ssl", "cacert.pem"),
				Path.Combine(SoberDataRoot, "asset_overlay", "ssl", "voidstrap-ca-bundle.pem")
			})
			{
				if (File.Exists(legacy))
					File.Delete(legacy);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The Sober trust store could not be restored: " + ex.Message);
		}
	}

	private static void RemoveCertificates()
	{
		HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
		AddVersionDirectories(directories, Paths.Versions);
		AddVersionDirectories(directories, Path.Combine(Paths.LocalAppData, "Roblox", "Versions"));
		if (Voidstrap.Utility.Platform.IsWindows)
			directories.Add(new Voidstrap.AppData.RobloxPlayerData().Directory);
		if (Voidstrap.Utility.Platform.IsMacOS && Voidstrap.Utility.MacRobloxBundle.Find() is string bundle)
			directories.Add(Path.Combine(bundle, "Contents", "Resources"));
		foreach (string directory in directories)
		{
			string path = Path.Combine(directory, "ssl", "cacert.pem");
			try
			{
				if (File.Exists(path))
					RemoveFromBundle(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "A Roblox trust bundle could not be cleaned: " + ex.Message);
			}
		}
		RemoveFromRootStore();
	}

	private static void AddVersionDirectories(HashSet<string> directories, string root)
	{
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
			return;
		if (File.Exists(Path.Combine(root, "RobloxPlayerBeta.exe")))
			directories.Add(root);
		foreach (string directory in Directory.EnumerateDirectories(root))
		{
			if (File.Exists(Path.Combine(directory, "RobloxPlayerBeta.exe")))
				directories.Add(directory);
		}
	}

	private static void RemoveFromBundle(string path)
	{
		string original = File.ReadAllText(path);
		StringBuilder cleaned = new(original.Length);
		int cursor = 0;
		bool removed = false;
		foreach (Match match in CertificatePattern().Matches(original))
		{
			cleaned.Append(original, cursor, match.Index - cursor);
			if (IsVoidstrapCertificate(match.Value))
				removed = true;
			else
				cleaned.Append(match.Value);
			cursor = match.Index + match.Length;
		}
		if (!removed)
			return;
		cleaned.Append(original, cursor, original.Length - cursor);
		FileAttributes attributes = File.GetAttributes(path);
		bool readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
		if (readOnly)
			File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		try
		{
			string temporary = path + ".voidstrap.tmp";
			File.WriteAllText(temporary, cleaned.ToString().TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
			File.Move(temporary, path, true);
		}
		finally
		{
			if (readOnly && File.Exists(path))
				File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
		}
	}

	private static bool IsVoidstrapCertificate(string pem)
	{
		try
		{
			using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
			return string.Equals(certificate.Subject, CaSubject, StringComparison.OrdinalIgnoreCase);
		}
		catch (System.Security.Cryptography.CryptographicException)
		{
			return false;
		}
	}

	private static void RemoveFromRootStore()
	{
		if (!OperatingSystem.IsWindows())
			return;
		try
		{
			using X509Store store = new(StoreName.Root, StoreLocation.CurrentUser);
			store.Open(OpenFlags.ReadWrite);
			X509Certificate2Collection present = store.Certificates;
			try
			{
				X509Certificate2[] owned = [.. present.Where(existing => string.Equals(existing.Subject, CaSubject, StringComparison.OrdinalIgnoreCase))];
				if (owned.Length > 0)
					Frontend.ShowMessageBox(Voidstrap.Resources.Strings.Bootstrapper_AssetWarpCertificateRemoval, System.Windows.MessageBoxImage.Information);
				foreach (X509Certificate2 existing in owned)
					store.Remove(existing);
			}
			finally
			{
				foreach (X509Certificate2 existing in present)
					existing.Dispose();
			}
		}
		catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or System.Security.SecurityException)
		{
			App.Logger?.WriteLine(LogIdent, "The AssetWarp certificate could not be removed from the root store: " + ex.Message);
		}
	}
}
