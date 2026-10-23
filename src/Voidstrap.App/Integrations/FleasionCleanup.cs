using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace Voidstrap.Integrations;

internal static partial class FleasionCleanup
{
	private const string LogIdent = "FleasionCleanup";
	private static readonly Version LastAffectedVersion = new(2, 4, 1);
	private const string CertificateName = "CN=Fleasion Proxy CA";
	private const string HostsMarker = "# Fleasion proxy entry";
	private static readonly string[] TaskNames = ["Fleasion_Autostart", "Fleasion-HostsWatchdog"];
	private static readonly string[] FirewallRules = ["Fleasion - Allow inbound (Private,Public)", "Fleasion - Allow outbound (Private,Public)"];
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string RunValue = "Fleasion";
	private const string StorePackageName = "ROBLOXCorporation.RobloxGDK";
	private static int _warned;

	internal sealed record Installation(Version? Version, IReadOnlyList<string> Executables);

	[GeneratedRegex(@"\[App\] Version (\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
	private static partial Regex LogVersionPattern();

	[GeneratedRegex(@"Fleasion-v(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex FileVersionPattern();

	[GeneratedRegex(@"-----BEGIN CERTIFICATE-----\s+[A-Za-z0-9+/=\s]+?-----END CERTIFICATE-----\s*", RegexOptions.CultureInvariant)]
	private static partial Regex PemBlockPattern();

	[GeneratedRegex(@"^[A-Za-z0-9._~-]+$", RegexOptions.CultureInvariant)]
	private static partial Regex PackageFullNamePattern();

	private static string ConfigDirectory(string localAppData) => Path.Combine(localAppData, "FleasionNT");

	private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Fleasion.lnk");

	internal static void WarnAtStartup()
	{
		if (!Voidstrap.Utility.Platform.IsWindows || App.State.Prop.FleasionWarningDismissedFor == LastAffectedVersion.ToString(3) || Interlocked.Exchange(ref _warned, 1) != 0)
			return;
		_ = Task.Run(WarnIfAffected);
	}

	private static void WarnIfAffected()
	{
		try
		{
			Installation? installation = Detect();
			if (installation is null || (installation.Version is not null && installation.Version > LastAffectedVersion))
				return;
			string version = installation.Version is null ? "an older version of Fleasion" : "Fleasion " + installation.Version.ToString(3);
			App.Logger.WriteLine(LogIdent, "Found " + version + ", which no longer works with Roblox");
			string message = "You have " + version + " installed. Fleasion is currently down, and version " + LastAffectedVersion.ToString(3) + " and older will break Roblox.\n\n"
				+ "Please uninstall Fleasion completely, then wait for an official fixed release before installing it again.\n\n"
				+ "Uninstall closes Fleasion and Roblox, then removes Fleasion, its settings and cache, its certificate, hosts file entries, firewall rules and startup tasks, and clears the Roblox asset cache it changed. Windows asks for administrator permission.";
			MessageBoxResult result = Frontend.ShowMessageBox(message, MessageBoxImage.Warning, "Uninstall Fleasion", "Don't remind me", "Not now");
			if (result == MessageBoxResult.Yes)
			{
				Frontend.ShowMessageBox(Uninstall(), MessageBoxImage.Information);
			}
			else if (result == MessageBoxResult.No)
			{
				App.State.Prop.FleasionWarningDismissedFor = LastAffectedVersion.ToString(3);
				App.State.Save();
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LogIdent, ex);
		}
	}

	internal static Installation? Detect()
	{
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		List<string> executables = FindExecutables();
		bool installed = Directory.Exists(ConfigDirectory(localAppData)) || executables.Count > 0 || File.Exists(StartMenuShortcut);
		if (!installed)
			return null;
		Version? version = ReadLoggedVersion(localAppData);
		if (version is null)
		{
			foreach (string executable in executables)
			{
				Match match = FileVersionPattern().Match(Path.GetFileName(executable));
				if (match.Success)
				{
					version = ToVersion(match);
					break;
				}
			}
		}
		return new Installation(version, executables);
	}

	private static Version ToVersion(Match match) => new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));

	private static Version? ReadLoggedVersion(string localAppData)
	{
		string log = Path.Combine(ConfigDirectory(localAppData), "logs", "fleasion.log");
		try
		{
			if (!File.Exists(log) || new FileInfo(log).Length > 16 * 1024 * 1024)
				return null;
			using FileStream stream = new(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new(stream);
			MatchCollection matches = LogVersionPattern().Matches(reader.ReadToEnd());
			return matches.Count == 0 ? null : ToVersion(matches[^1]);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static List<string> FindExecutables()
	{
		HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
		string managed = Path.Combine(Paths.Fleasion, "Fleasion.exe");
		if (File.Exists(managed))
			found.Add(managed);
		string? autostart = ReadAutostartExecutable();
		if (autostart is not null)
			found.Add(autostart);
		foreach (Process process in FindProcesses())
		{
			using (process)
			{
				try
				{
					string? path = process.MainModule?.FileName;
					if (path is not null && File.Exists(path))
						found.Add(path);
				}
				catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
				{
				}
			}
		}
		return [.. found];
	}

	private static string? ReadAutostartExecutable()
	{
		try
		{
			using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
			if (key?.GetValue(RunValue) is not string command || command.Length == 0)
				return null;
			string path = command.StartsWith('"') ? command[1..Math.Max(1, command.IndexOf('"', 1))] : command.Split(' ', 2)[0];
			return File.Exists(path) && IsFleasionExecutable(path) ? path : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
		{
			return null;
		}
	}

	private static bool IsFleasionExecutable(string path)
	{
		string name = Path.GetFileName(path);
		return name.StartsWith("Fleasion", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
	}

	private static Process[] FindProcesses()
	{
		List<Process> matches = [];
		foreach (Process process in Process.GetProcesses())
		{
			string name;
			try
			{
				name = process.ProcessName;
			}
			catch (InvalidOperationException)
			{
				process.Dispose();
				continue;
			}
			if (name.Equals("Fleasion", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Fleasion-", StringComparison.OrdinalIgnoreCase))
				matches.Add(process);
			else
				process.Dispose();
		}
		return [.. matches];
	}

	private static void CloseProcesses(Process[] processes)
	{
		foreach (Process process in processes)
		{
			using (process)
			{
				try
				{
					process.Kill(entireProcessTree: true);
					process.WaitForExit(5000);
				}
				catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
				{
					App.Logger.WriteLine(LogIdent, "Could not close " + process.Id + ": " + ex.Message);
				}
			}
		}
	}

	internal static string Uninstall()
	{
		App.Logger.WriteLine(LogIdent, "Uninstalling Fleasion");
		List<string> problems = [];
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		List<string> executables = FindExecutables();

		if (App.Settings.Prop.Fleasion)
		{
			App.Settings.Prop.Fleasion = false;
			App.Settings.Save();
		}
		CloseProcesses(FindProcesses());
		CloseProcesses(Process.GetProcessesByName("RobloxPlayerBeta"));

		if (!RunElevatedHelper(localAppData))
			problems.Add("the certificate, hosts file entries, firewall rules and startup tasks (administrator permission was not given)");

		try
		{
			using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
			key?.DeleteValue(RunValue, throwOnMissingValue: false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			problems.Add("the Fleasion startup entry");
		}

		RemoveCertificates(StoreLocation.CurrentUser, problems);
		DisableStorePackageDebugging();

		if (!TryDelete(StartMenuShortcut))
			problems.Add("the Start menu shortcut");
		if (!TryDeleteDirectory(ConfigDirectory(localAppData)))
			problems.Add("Fleasion's settings and cache folder");
		if (!TryDeleteDirectory(Paths.Fleasion))
			problems.Add("the Fleasion copy Voidstrap downloaded");
		foreach (string executable in executables)
		{
			if (IsFleasionExecutable(executable) && !TryDelete(executable))
				problems.Add(executable);
		}
		if (!Voidstrap.Utility.RobloxAssetCache.ClearStorage())
			problems.Add("part of the Roblox asset cache (close Roblox and try again)");

		App.Logger.WriteLine(LogIdent, problems.Count == 0 ? "Fleasion was removed completely" : "Fleasion was removed except: " + string.Join("; ", problems));
		return problems.Count == 0
			? "Fleasion was removed completely, and its cache and the Roblox asset cache were reset."
			: "Fleasion was removed, but Voidstrap could not remove:\n\n" + string.Join("\n", problems);
	}

	private static bool RunElevatedHelper(string localAppData)
	{
		string? processPath = Paths.Process;
		if (string.IsNullOrEmpty(processPath))
			return false;
		try
		{
			ProcessStartInfo info = new()
			{
				FileName = processPath,
				UseShellExecute = true,
				Verb = "runas",
				Arguments = "-fleasionuninstall \"" + localAppData + "\""
			};
			using Process? process = Process.Start(info);
			if (process is null)
				return false;
			if (!process.WaitForExit(120000))
			{
				App.Logger.WriteLine(LogIdent, "The elevated Fleasion cleanup timed out");
				return false;
			}
			return process.ExitCode == 0;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			App.Logger.WriteLine(LogIdent, "Elevation was declined");
			return false;
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			App.Logger.WriteLine(LogIdent, "The elevated Fleasion cleanup could not start: " + ex.Message);
			return false;
		}
	}

	internal static int RunElevatedCleanup(string? userLocalAppData)
	{
		List<string> problems = [];
		foreach (string task in TaskNames)
			RunTool("schtasks.exe", ["/Delete", "/TN", task, "/F"]);
		foreach (string rule in FirewallRules)
			RunTool("netsh.exe", ["advfirewall", "firewall", "delete", "rule", "name=" + rule]);

		string hosts = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
		try
		{
			if (File.Exists(hosts))
			{
				string[] lines = File.ReadAllLines(hosts);
				string[] kept = [.. lines.Where(line => !line.Contains(HostsMarker, StringComparison.Ordinal))];
				if (kept.Length != lines.Length)
					File.WriteAllLines(hosts, kept);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			problems.Add("hosts file: " + ex.Message);
		}

		RemoveCertificates(StoreLocation.LocalMachine, problems);

		string localAppData = !string.IsNullOrWhiteSpace(userLocalAppData) && Path.IsPathFullyQualified(userLocalAppData) && Directory.Exists(userLocalAppData)
			? userLocalAppData
			: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		foreach (string bundle in FindRobloxCertificateBundles(localAppData))
			StripFleasionCertificates(bundle, problems);

		foreach (string problem in problems)
			App.Logger.WriteLine(LogIdent, "Elevated cleanup could not finish: " + problem);
		return problems.Count == 0 ? 0 : 1;
	}

	private static void RunTool(string tool, string[] arguments)
	{
		try
		{
			ProcessStartInfo info = new(Path.Combine(Environment.SystemDirectory, tool))
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (string argument in arguments)
				info.ArgumentList.Add(argument);
			using Process? process = Process.Start(info);
			if (process is null)
				return;
			_ = process.StandardOutput.ReadToEnd();
			_ = process.StandardError.ReadToEnd();
			process.WaitForExit(20000);
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			App.Logger.WriteLine(LogIdent, tool + " could not run: " + ex.Message);
		}
	}

	private static void RemoveCertificates(StoreLocation location, List<string> problems)
	{
		try
		{
			using X509Store store = new(StoreName.Root, location);
			store.Open(OpenFlags.ReadWrite);
			foreach (X509Certificate2 certificate in store.Certificates)
			{
				using (certificate)
				{
					if (certificate.Subject.Contains(CertificateName, StringComparison.OrdinalIgnoreCase))
						store.Remove(certificate);
				}
			}
		}
		catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
		{
			problems.Add("the Fleasion certificate in the " + (location == StoreLocation.LocalMachine ? "computer" : "user") + " certificate store");
		}
	}

	private static IEnumerable<string> FindRobloxCertificateBundles(string localAppData)
	{
		HashSet<string> versionDirectories = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			string saved = Path.Combine(ConfigDirectory(localAppData), "roblox_dirs.json");
			if (File.Exists(saved))
			{
				using JsonDocument document = JsonDocument.Parse(File.ReadAllText(saved));
				if (document.RootElement.TryGetProperty("roblox_dirs", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement entry in list.EnumerateArray())
					{
						if (entry.GetString() is string directory && Path.IsPathFullyQualified(directory))
							versionDirectories.Add(directory);
					}
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
		}

		string[] versionRoots =
		[
			Path.Combine(localAppData, "Roblox", "Versions"),
			Path.Combine(localAppData, "Voidstrap", "RblxVersions"),
			Path.Combine(localAppData, "Bloxstrap", "Versions"),
			Path.Combine(localAppData, "Fishstrap", "Versions"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions"),
			Paths.Versions
		];
		foreach (string root in versionRoots)
		{
			if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
				continue;
			try
			{
				foreach (string directory in Directory.EnumerateDirectories(root))
					versionDirectories.Add(directory);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		foreach (string directory in versionDirectories)
		{
			string bundle = Path.Combine(directory, "ssl", "cacert.pem");
			if (File.Exists(bundle))
				yield return bundle;
		}
	}

	private static void StripFleasionCertificates(string bundle, List<string> problems)
	{
		try
		{
			string text = File.ReadAllText(bundle);
			StringBuilder kept = new(text.Length);
			int position = 0;
			bool removed = false;
			foreach (Match block in PemBlockPattern().Matches(text))
			{
				kept.Append(text, position, block.Index - position);
				position = block.Index + block.Length;
				if (IsFleasionCertificate(block.Value))
					removed = true;
				else
					kept.Append(block.Value);
			}
			kept.Append(text, position, text.Length - position);
			if (removed)
			{
				File.WriteAllText(bundle, kept.ToString());
				App.Logger.WriteLine(LogIdent, "Removed the Fleasion certificate from " + bundle);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			problems.Add(bundle + ": " + ex.Message);
		}
	}

	private static bool IsFleasionCertificate(string pem)
	{
		try
		{
			using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
			return certificate.Subject.Contains(CertificateName, StringComparison.OrdinalIgnoreCase);
		}
		catch (System.Security.Cryptography.CryptographicException)
		{
			return false;
		}
	}

	private static void DisableStorePackageDebugging()
	{
		string? packageFullName = FindStorePackageFullName();
		if (packageFullName is null)
			return;
		Guid classId = new("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D");
		Guid interfaceId = new("F27C3930-8029-4AD1-94E3-3DBA417810C1");
		_ = CoInitializeEx(0, 0);
		if (CoCreateInstance(in classId, 0, 1, in interfaceId, out nint settings) < 0 || settings == 0)
			return;
		unsafe
		{
			nint* table = *(nint**)settings;
			int result;
			fixed (char* name = packageFullName)
				result = ((delegate* unmanaged[Stdcall]<nint, char*, int>)table[4])(settings, name);
			_ = ((delegate* unmanaged[Stdcall]<nint, uint>)table[2])(settings);
			App.Logger.WriteLine(LogIdent, "Store Roblox package debugging cleanup returned 0x" + result.ToString("X8"));
		}
	}

	private static string? FindStorePackageFullName()
	{
		try
		{
			ProcessStartInfo info = new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true
			};
			foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "(Get-AppxPackage -Name '" + StorePackageName + "' | Select-Object -First 1).PackageFullName" })
				info.ArgumentList.Add(argument);
			using Process? process = Process.Start(info);
			if (process is null)
				return null;
			string output = process.StandardOutput.ReadToEnd().Trim();
			process.WaitForExit(20000);
			return PackageFullNamePattern().IsMatch(output) ? output : null;
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			return null;
		}
	}

	private static bool TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
					File.SetAttributes(file, FileAttributes.Normal);
				Directory.Delete(path, recursive: true);
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	[LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx")]
	private static partial int CoInitializeEx(nint reserved, uint coInit);

	[LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
	private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);
}
