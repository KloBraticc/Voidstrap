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

	public static async Task<string> CreateAsync(CancellationToken token = default)
	{
		App.Logger.WriteLine(LOG_IDENT, "Creating a support file");
		App.Logger.Flush();
		Redactor redactor = Redactor.Create();
		List<FileInfo> logs = await Task.Run(CollectLogs, token).ConfigureAwait(false);
		string crashes = await Task.Run(ReadCrashRecords, token).ConfigureAwait(false);

		Directory.CreateDirectory(Paths.Desktop);
		string fileName = "Voidstrap support " + DateTime.Now.ToString("yyyyMMdd HHmm", CultureInfo.InvariantCulture) + ".zip";
		string target = Path.Combine(Paths.Desktop, fileName);
		string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await Task.Run(() =>
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
				Add(zip, "Windows crash records.txt", redactor.Apply(crashes));
				Add(zip, "README.txt", redactor.Apply(BuildSummary(logs, logTexts, crashes)));
			}, token).ConfigureAwait(false);
			File.Move(temporary, target, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
		App.Logger.WriteLine(LOG_IDENT, "Support file created with " + logs.Count + " log(s)");
		return target;
	}

	private static List<FileInfo> CollectLogs()
	{
		List<FileInfo> logs = new();
		foreach (string folder in new[] { Paths.Logs, Paths.TempLogs }.Where(static folder => !string.IsNullOrWhiteSpace(folder)).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				if (Directory.Exists(folder))
					logs.AddRange(new DirectoryInfo(folder).EnumerateFiles("Voidstrap_*.log"));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
		return logs
			.Where(static log => log.Length <= MaxLogBytes)
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
			return "This log could not be read: " + ex.Message;
		}
	}

	private static void Add(ZipArchive zip, string name, string content)
	{
		ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
		using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
		writer.Write(content);
	}

	private static string ReadCrashRecords()
	{
		if (!OperatingSystem.IsWindows())
			return "Crash records are only collected on Windows.";
		try
		{
			long window = (long)CrashLookback.TotalMilliseconds;
			ProcessStartInfo start = new("wevtutil.exe")
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			foreach (string argument in new[] { "qe", "Application", "/rd:true", "/c:400", "/f:text", "/q:*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= " + window.ToString(CultureInfo.InvariantCulture) + "]]]" })
				start.ArgumentList.Add(argument);
			using Process? process = Process.Start(start);
			if (process == null)
				return "Windows crash records could not be read.";
			Task<string> output = process.StandardOutput.ReadToEndAsync();
			if (!process.WaitForExit(15000))
			{
				try { process.Kill(); } catch (InvalidOperationException) { }
				return "Reading Windows crash records timed out.";
			}
			List<string> relevant = output.Result
				.Split("Event[", StringSplitOptions.RemoveEmptyEntries)
				.Where(static record => record.Contains("Voidstrap", StringComparison.OrdinalIgnoreCase) || record.Contains("Roblox", StringComparison.OrdinalIgnoreCase) || record.Contains("rishade", StringComparison.OrdinalIgnoreCase))
				.Take(40)
				.Select(static record => "Event[" + record.TrimEnd())
				.ToList();
			return relevant.Count == 0
				? "Windows recorded no Voidstrap or Roblox errors in the last " + CrashLookback.Days + " days."
				: relevant.Count + " error record(s) from the last " + CrashLookback.Days + " days:" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, relevant);
		}
		catch (Exception ex)
		{
			return "Windows crash records could not be read: " + ex.Message;
		}
	}

	private static string BuildSummary(List<FileInfo> logs, List<string> logTexts, string crashes)
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
		foreach (Match match in Regex.Matches(all, @"The previous session \(process \d+, started ([^,]+), log ([^)]+)\) ended without shutting down cleanly"))
			findings.Add("A Voidstrap session that started " + match.Groups[1].Value + " closed unexpectedly (it most likely crashed). Look at " + match.Groups[2].Value + ".");
		int freezes = Regex.Matches(all, @"\[UiFreeze\] The window was frozen for").Count;
		int stuck = Regex.Matches(all, @"\[UiFreeze\] The window has not responded").Count;
		if (freezes + stuck > 0)
			findings.Add("The Voidstrap window froze " + (freezes + stuck) + " time(s)" + (stuck > 0 ? ", and " + stuck + " of those lasted over 5 seconds" : "") + ". Search the logs for UiFreeze.");
		int windowsCrashes = Regex.Matches(crashes, @"^Event\[", RegexOptions.Multiline).Count;
		if (windowsCrashes > 0)
			findings.Add("Windows recorded " + windowsCrashes + " error record(s) mentioning Voidstrap or Roblox in the last " + CrashLookback.Days + " days. See Windows crash records.txt.");
		int crashLines = Regex.Matches(all, @"\[App::(DomainUnhandledException|GlobalExceptionHandler|UnobservedTaskException)\]").Count;
		if (crashLines > 0)
			findings.Add("Voidstrap logged " + crashLines + " unexpected error(s) it could not recover from cleanly. Search the logs for UnhandledException.");
		int bindingErrors = Regex.Matches(all, @"\[(?:BindingErrors|ResourceErrors|AnimationErrors)\] ([^
]*)").Select(static match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Count();
		if (bindingErrors > 0)
			findings.Add(bindingErrors + " different interface warning(s) were logged, where part of a page may not show its data. Search the logs for BindingErrors.");
		if (findings.Count == 0)
			findings.Add("No obvious problems were found. The logs are still useful, so please send them anyway.");
		foreach (string finding in findings)
			text.AppendLine("* " + finding);
		text.AppendLine();
		text.AppendLine("FILES IN THIS ZIP");
		text.AppendLine("* logs: the " + logs.Count + " most recent Voidstrap logs, newest first: " + string.Join(", ", logs.Select(static log => log.Name)));
		text.AppendLine("* Windows crash records.txt: crashes and errors Windows recorded for Voidstrap and Roblox");
		text.AppendLine();
		text.AppendLine("FOR DEVELOPERS");
		text.AppendLine("* HiddenErrors: errors Voidstrap caught and handled, with the code that caused them");
		text.AppendLine("* UiFreeze: times the window stopped responding");
		text.AppendLine("* App::Diagnostics: system details and sessions that ended uncleanly");
		return text.ToString();
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
			redactor._rules.Add((new Regex(@"(gameinfo|launchtime|browsertrackerid|authticket|ticket)[:=][^\s+&""']+", RegexOptions.IgnoreCase), "$1:[removed]"));
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
			_rules.Add((new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase), replacement));
		}

		public string Apply(string text)
		{
			foreach ((Regex pattern, string replacement) in _rules)
				text = pattern.Replace(text, replacement);
			return text;
		}
	}
}
