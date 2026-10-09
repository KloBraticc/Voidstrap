using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Voidstrap.Utility;

internal static partial class MacRobloxBundle
{
	public static string? Find()
	{
		string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Roblox.app");
		return new[] { "/Applications/Roblox.app", user }.FirstOrDefault(Directory.Exists);
	}

	public static string? ReadVersion(string bundle)
	{
		string plist = Path.Combine(bundle, "Contents", "Info.plist");
		if (!File.Exists(plist))
			return null;
		try
		{
			Match match = VersionPattern().Match(File.ReadAllText(plist));
			if (match.Success)
				return match.Groups[1].Value.Trim();
			ProcessStartInfo info = new("/usr/libexec/PlistBuddy") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
			info.ArgumentList.Add("-c");
			info.ArgumentList.Add("Print :CFBundleShortVersionString");
			info.ArgumentList.Add(plist);
			using Process? process = Process.Start(info);
			if (process == null)
				return null;
			string output = process.StandardOutput.ReadToEnd().Trim();
			return process.WaitForExit(5000) && process.ExitCode == 0 && output.Length > 0 ? output : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}
	}

	[GeneratedRegex("<key>CFBundleShortVersionString</key>\\s*<string>([^<]+)</string>")]
	private static partial Regex VersionPattern();
}
