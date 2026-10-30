using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class SupportBundle
{
	private const string LOG_IDENT = "SupportBundle";

	private const int MaxLogs = 6;

	private const long MaxLogBytes = 8L * 1024 * 1024;

	private static readonly TimeSpan CrashLookback = TimeSpan.FromDays(14);

	private const string CrashFileName = "Crash records.txt";

	private static readonly string[] CrashKeywords = ["Voidstrap", "Roblox", "rishade", "sober"];

	private sealed record CrashRecords(string Text, int Count);

	public static async Task<string> CreateAsync(CancellationToken token = default)
	{
		App.Logger.WriteLine(LOG_IDENT, "Creating a support file");
		App.Logger.Flush();
		Redactor redactor = Redactor.Create();
		List<FileInfo> logs = await Task.Run(CollectLogs, token).ConfigureAwait(false);
		CrashRecords crashes = await Task.Run(ReadCrashRecords, token).ConfigureAwait(false);

		string fileName = "Voidstrap support " + DateTime.Now.ToString("yyyyMMdd HHmm", CultureInfo.InvariantCulture) + ".zip";
		Exception? lastError = null;
		foreach (string folder in OutputFolders())
		{
			try
			{
				string target = Path.Combine(folder, fileName);
				await WriteZipAsync(target, logs, crashes, redactor, token).ConfigureAwait(false);
				App.Logger.WriteLine(LOG_IDENT, "Support file created with " + logs.Count + " log(s)");
				return target;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				lastError = ex;
				App.Logger.WriteLine(LOG_IDENT, "The support file could not be saved in one location, trying the next: " + ex.Message);
			}
		}
		throw lastError ?? new IOException("No folder was available for the support file.");
	}

	private static IEnumerable<string> OutputFolders()
	{
		foreach (string folder in new[] { Paths.Desktop, Paths.Logs, Paths.TempLogs })
		{
			if (string.IsNullOrWhiteSpace(folder))
				continue;
			bool usable;
			try
			{
				Directory.CreateDirectory(folder);
				usable = true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
				usable = false;
			}
			if (usable)
				yield return folder;
		}
	}

	private static async Task WriteZipAsync(
		string target,
		List<FileInfo> logs,
		CrashRecords crashes,
		Redactor redactor,
		CancellationToken token
	)
	{
		string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await Task.Run(
					() =>
					{
						using FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write);
						using ZipArchive zip = new(stream, ZipArchiveMode.Create);
						List<string> logTexts = new();
						foreach (FileInfo log in logs)
						{
							token.ThrowIfCancellationRequested();
							string text = redactor.Apply(ReadShared(log.FullName));
							logTexts.Add(text);
							Add(zip, "logs/" + log.Name, text);
						}
						Add(zip, CrashFileName, redactor.Apply(crashes.Text));
						Add(zip, "README.txt", redactor.Apply(BuildSummary(logs, logTexts, crashes)));
					},
					token
				)
				.ConfigureAwait(false);
			File.Move(temporary, target, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static List<FileInfo> CollectLogs()
	{
		List<FileInfo> logs = new();
		foreach (
			string folder in new[] { Paths.Logs, Paths.TempLogs }
				.Where(static folder => !string.IsNullOrWhiteSpace(folder))
				.Distinct(StringComparer.OrdinalIgnoreCase)
		)
		{
			try
			{
				if (Directory.Exists(folder))
					logs.AddRange(new DirectoryInfo(folder).EnumerateFiles("Voidstrap_*.log"));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
		}
		return logs.Where(static log => log.Length <= MaxLogBytes)
			.OrderByDescending(static log => log.LastWriteTimeUtc)
			.Take(MaxLogs)
			.ToList();
	}

	private static string ReadShared(string path)
	{
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new(stream, Encoding.UTF8);
			return reader.ReadToEnd();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return "This file could not be read: " + ex.Message;
		}
	}

	private static void Add(ZipArchive zip, string name, string content)
	{
		ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
		using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
		writer.Write(content);
	}

	private static bool MentionsVoidstrap(string text)
	{
		return CrashKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
	}

	private static CrashRecords ReadCrashRecords()
	{
		try
		{
			if (OperatingSystem.IsWindows())
				return ReadWindowsCrashRecords();
			if (OperatingSystem.IsMacOS())
				return ReadMacCrashReports();
			if (OperatingSystem.IsLinux())
				return ReadLinuxCoreDumps();
			return new CrashRecords("Crash records are not collected on this system.", 0);
		}
		catch (Exception ex)
		{
			return new CrashRecords("Crash records could not be read: " + ex.Message, 0);
		}
	}

	private static string? RunTool(string fileName, IEnumerable<string> arguments)
	{
		ProcessStartInfo start = new(fileName)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (string argument in arguments)
			start.ArgumentList.Add(argument);
		using Process? process = Process.Start(start);
		if (process == null)
			return null;
		Task<string> output = process.StandardOutput.ReadToEndAsync();
		_ = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(15000))
		{
			try
			{
				process.Kill();
			}
			catch (InvalidOperationException) { }
			return null;
		}
		return output.Result;
	}

	private static CrashRecords ReadWindowsCrashRecords()
	{
		long window = (long)CrashLookback.TotalMilliseconds;
		string? output = RunTool(
			"wevtutil.exe",
			[
				"qe",
				"Application",
				"/rd:true",
				"/c:400",
				"/f:text",
				"/q:*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= "
					+ window.ToString(CultureInfo.InvariantCulture)
					+ "]]]",
			]
		);
		if (output == null)
			return new CrashRecords("Windows crash records could not be read.", 0);
		List<string> relevant = output
			.Split("Event[", StringSplitOptions.RemoveEmptyEntries)
			.Where(MentionsVoidstrap)
			.Take(40)
			.Select(static record => "Event[" + record.TrimEnd())
			.ToList();
		return Describe(relevant, "error record(s) Windows recorded");
	}

	private static CrashRecords ReadMacCrashReports()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		DateTime since = DateTime.UtcNow - CrashLookback;
		List<string> relevant = new();
		foreach (string folder in new[] { Path.Combine(home, "Library", "Logs", "DiagnosticReports"), "/Library/Logs/DiagnosticReports" })
		{
			if (!Directory.Exists(folder))
				continue;
			IEnumerable<FileInfo> reports;
			try
			{
				reports = new DirectoryInfo(folder)
					.EnumerateFiles()
					.Where(static file => file.Extension is ".ips" or ".crash" or ".hang" or ".diag" or ".spin")
					.Where(file => file.LastWriteTimeUtc >= since && MentionsVoidstrap(file.Name))
					.OrderByDescending(static file => file.LastWriteTimeUtc)
					.Take(10)
					.ToList();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				continue;
			}
			foreach (FileInfo report in reports)
			{
				string content = ReadShared(report.FullName);
				string[] lines = content.Split('\n');
				relevant.Add("Report " + report.Name + "\n" + string.Join("\n", lines.Take(250)));
			}
		}
		return Describe(relevant, "crash report(s) macOS recorded");
	}

	private static CrashRecords ReadLinuxCoreDumps()
	{
		string? output = null;
		try
		{
			output = RunTool("coredumpctl", ["list", "--no-pager", "--no-legend"]);
		}
		catch (System.ComponentModel.Win32Exception) { }
		if (output == null && Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed)
		{
			try
			{
				output = RunTool("flatpak-spawn", ["--host", "coredumpctl", "list", "--no-pager", "--no-legend"]);
			}
			catch (System.ComponentModel.Win32Exception) { }
		}
		if (output == null)
			return new CrashRecords("Crash records are not available here, coredumpctl could not be run.", 0);
		DateTime since = DateTime.Now - CrashLookback;
		List<string> relevant = output
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Where(MentionsVoidstrap)
			.Where(line => !TryReadCoreDumpTime(line, out DateTime when) || when >= since)
			.Take(40)
			.ToList();
		return Describe(relevant, "core dump(s) the system recorded");
	}

	private static bool TryReadCoreDumpTime(string line, out DateTime when)
	{
		string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		when = default;
		return parts.Length >= 3
			&& DateTime.TryParse(parts[1] + " " + parts[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out when);
	}

	private static CrashRecords Describe(List<string> relevant, string label)
	{
		if (relevant.Count == 0)
			return new CrashRecords(
				"No Voidstrap or Roblox " + label.Replace("(s)", "s") + " in the last " + CrashLookback.Days + " days.",
				0
			);
		return new CrashRecords(
			relevant.Count
				+ " "
				+ label
				+ " in the last "
				+ CrashLookback.Days
				+ " days:"
				+ Environment.NewLine
				+ Environment.NewLine
				+ string.Join(Environment.NewLine + Environment.NewLine, relevant),
			relevant.Count
		);
	}

	private static string BuildSummary(List<FileInfo> logs, List<string> logTexts, CrashRecords crashes)
	{
		StringBuilder text = new();
		text.AppendLine("VOIDSTRAP SUPPORT FILE");
		text.AppendLine("Send this whole zip file to whoever is helping you. Personal details were removed.");
		text.AppendLine();
		text.AppendLine("Created " + DateTime.UtcNow.ToString("yyyy'.'MM'.'dd HH':'mm", CultureInfo.InvariantCulture) + " UTC");
		string commit = App.BuildMetadata.CommitHash ?? "";
		text.AppendLine("Voidstrap " + App.Version + (string.IsNullOrWhiteSpace(commit) ? "" : " (commit " + commit + ")"));
		foreach (string line in LogDiagnostics.EnvironmentLines())
			text.AppendLine(line);
		text.AppendLine();
		text.AppendLine("WHAT WENT WRONG RECENTLY");
		List<string> findings = new();
		string all = string.Join("\n", logTexts);
		foreach (
			Match match in Regex.Matches(
				all,
				@"The previous session \(process (\d+), started ([^,]+), log ([^)]+)\) ended without shutting down cleanly"
			)
		)
		{
			bool crashRecorded = CrashMentionsProcess(crashes.Text, match.Groups[1].Value);
			findings.Add(
				"A Voidstrap session that started "
					+ match.Groups[2].Value
					+ (
						crashRecorded
							? " crashed, and the system recorded the crash. Look at "
								+ match.Groups[3].Value
								+ " and "
								+ CrashFileName
								+ "."
							: " ended suddenly without a crash report, so it was most likely closed by force, for example with End task. Look at "
								+ match.Groups[3].Value
								+ "."
					)
			);
		}
		int freezes = Regex.Matches(all, @"\[UiFreeze\] The window was frozen for").Count;
		int stuck = Regex.Matches(all, @"\[UiFreeze\] The window has not responded").Count;
		if (freezes + stuck > 0)
			findings.Add(
				"The Voidstrap window froze "
					+ (freezes + stuck)
					+ " time(s)"
					+ (stuck > 0 ? ", and " + stuck + " of those lasted over 5 seconds" : "")
					+ ". Search the logs for UiFreeze."
			);
		if (crashes.Count > 0)
			findings.Add(
				"The system recorded "
					+ crashes.Count
					+ " crash or error record(s) mentioning Voidstrap or Roblox in the last "
					+ CrashLookback.Days
					+ " days. See "
					+ CrashFileName
					+ "."
			);
		int crashLines = Regex.Matches(all, @"\[App::(DomainUnhandledException|GlobalExceptionHandler|UnobservedTaskException)\]").Count;
		if (crashLines > 0)
			findings.Add(
				"Voidstrap logged "
					+ crashLines
					+ " unexpected error(s) it could not recover from cleanly. Search the logs for UnhandledException."
			);
		int bindingErrors = Regex
			.Matches(
				all,
				@"\[(?:BindingErrors|ResourceErrors|AnimationErrors)\] ([^
]*)"
			)
			.Select(static match => match.Groups[1].Value)
			.Distinct(StringComparer.Ordinal)
			.Count();
		if (bindingErrors > 0)
			findings.Add(
				bindingErrors
					+ " different interface warning(s) were logged, where part of a page may not show its data. Search the logs for BindingErrors."
			);
		if (findings.Count == 0)
			findings.Add("No obvious problems were found. The logs are still useful, so please send them anyway.");
		foreach (string finding in findings)
			text.AppendLine("* " + finding);
		text.AppendLine();
		text.AppendLine("FILES IN THIS ZIP");
		text.AppendLine(
			"* logs: the "
				+ logs.Count
				+ " most recent Voidstrap logs, newest first: "
				+ string.Join(", ", logs.Select(static log => log.Name))
		);
		text.AppendLine("* " + CrashFileName + ": crashes and errors the system recorded for Voidstrap and Roblox");
		text.AppendLine();
		text.AppendLine("FOR DEVELOPERS");
		text.AppendLine("* HiddenErrors: errors Voidstrap caught and handled, with the code that caused them");
		text.AppendLine("* UiFreeze: times the window stopped responding");
		text.AppendLine("* App::Diagnostics: system details and sessions that ended uncleanly");
		return text.ToString();
	}

	private static bool CrashMentionsProcess(string crashText, string pid)
	{
		if (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
			return false;
		string hex = "0x" + id.ToString("x", CultureInfo.InvariantCulture);
		return crashText.Contains(hex, StringComparison.OrdinalIgnoreCase)
			|| Regex.IsMatch(crashText, @"(?<!\d)" + id.ToString(CultureInfo.InvariantCulture) + @"(?!\d)");
	}

	private sealed class Redactor
	{
		private readonly List<(Regex Pattern, string Replacement)> _rules = new();

		public static Redactor Create()
		{
			Redactor redactor = new();
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (!string.IsNullOrWhiteSpace(profile))
				redactor.AddLiteral(profile, OperatingSystem.IsWindows() ? "%UserProfile%" : "~");
			redactor.AddWord(Environment.UserName, "[user]");
			redactor.AddWord(Environment.MachineName, "[computer]");
			try
			{
				(IReadOnlyList<string> names, IReadOnlyList<long> ids) = SavedAccounts.KnownIdentities();
				foreach (string name in names.OrderByDescending(static name => name.Length))
					redactor.AddWord(name, "[account]");
				foreach (long id in ids)
					redactor.AddWord(id.ToString(CultureInfo.InvariantCulture), "[account id]");
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "Saved account names could not be read for redaction: " + ex.Message);
			}
			redactor._rules.Add((new Regex(@"\.ROBLOSECURITY[^\s;""']*", RegexOptions.IgnoreCase), "[removed cookie]"));
			redactor._rules.Add(
				(
					new Regex(
						@"(""(?:crashReporterKey|sleepWakeUUID|bootSessionUUID|deviceIdentifierForVendor|incident|incident_id|machineID)""\s*:\s*"")[^""]*",
						RegexOptions.IgnoreCase
					),
					"$1[removed]"
				)
			);
			redactor._rules.Add(
				(
					new Regex(
						@"^((?:Anonymous UUID|Sleep/Wake UUID|Incident Identifier|CrashReporter Key|Hardware UUID):\s*).+$",
						RegexOptions.IgnoreCase | RegexOptions.Multiline
					),
					"$1[removed]"
				)
			);
			redactor._rules.Add(
				(
					new Regex(@"(gameinfo|launchtime|browsertrackerid|authticket|ticket)[:=][^\s+&""']+", RegexOptions.IgnoreCase),
					"$1:[removed]"
				)
			);
			return redactor;
		}

		private void AddLiteral(string value, string replacement)
		{
			_rules.Add((new Regex(Regex.Escape(value), RegexOptions.IgnoreCase), replacement));
		}

		private void AddWord(string? value, string replacement)
		{
			if (string.IsNullOrWhiteSpace(value) || value.Length < 3)
				return;
			_rules.Add(
				(new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase), replacement)
			);
		}

		public string Apply(string text)
		{
			foreach ((Regex pattern, string replacement) in _rules)
				text = pattern.Replace(text, replacement);
			return text;
		}
	}
}
