using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;
#if CROSSPLAT
using Tmds.DBus;
#endif

namespace Voidstrap.Utility;

internal static class LinuxBootstrapperSources
{
	private const int HostReadLimit = 4 * 1024 * 1024 - 1;

	private static readonly string[] PrefixRoots =
	[
		"$WINEPREFIX",
		"~/.wine",
		"~/.local/share/wineprefixes/*",
		"~/Games/*",
		"~/Games/Heroic/Prefixes/*",
		"~/Games/Heroic/Prefixes/*/*",
		"~/.local/share/bottles/bottles/*",
		"~/.var/app/com.usebottles.bottles/data/bottles/bottles/*",
		"~/.steam/steam/steamapps/compatdata/*/pfx",
		"~/.local/share/Steam/steamapps/compatdata/*/pfx",
		"~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/compatdata/*/pfx",
		"~/.var/app/com.valvesoftware.Steam/data/Steam/steamapps/compatdata/*/pfx",
		"~/.PlayOnLinux/wineprefix/*",
		"~/.local/share/grapejuice/prefixes/*",
		"~/.local/share/lutris/prefixes/*"
	];

	private static readonly string[] PrefixProfiles =
	[
		"drive_c/users/*/AppData/Local",
		"drive_c/users/*/Local Settings/Application Data"
	];

	private static readonly string[] DriveRoots =
	[
		"/run/media/$USER/*",
		"/media/$USER/*",
		"/media/*",
		"/mnt/*",
		"/mnt/*/*"
	];

	public static async Task<string?> FindSettingsAsync(string source, CancellationToken token)
	{
		List<string> patterns = [];
		foreach (string prefix in PrefixRoots)
			foreach (string profile in PrefixProfiles)
				patterns.Add(prefix + "/" + profile + "/" + source + "/Settings.json");
		foreach (string drive in DriveRoots)
			patterns.Add(drive + "/Users/*/AppData/Local/" + source + "/Settings.json");
		patterns.Add("$XDG_DATA_HOME/" + source + "/Settings.json");
		patterns.Add("~/.local/share/" + source + "/Settings.json");

		StringBuilder script = new("for f in");
		foreach (string pattern in patterns)
			script.Append(' ').Append(ToShellWord(pattern));
		script.Append("; do if [ -f \"$f\" ] && [ -r \"$f\" ]; then printf '%s\\t%s\\n' \"$(stat -c %Y -- \"$f\" 2>/dev/null || echo 0)\" \"$f\"; fi; done");

		ProcessExecution? result = await RunHostAsync("sh", ["-c", script.ToString()], token).ConfigureAwait(false);
		if (result is null || result.ExitCode != 0)
			return null;

		string? newest = null;
		long newestTime = long.MinValue;
		foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			int tab = line.IndexOf('\t');
			if (tab <= 0)
				continue;
			string path = line[(tab + 1)..].TrimEnd('\r');
			if (!Path.IsPathRooted(path))
				continue;
			long.TryParse(line.AsSpan(0, tab), NumberStyles.Integer, CultureInfo.InvariantCulture, out long modified);
			if (newest is null || modified > newestTime)
			{
				newest = path;
				newestTime = modified;
			}
		}

		if (newest is not null)
			App.Logger.WriteLine("BootstrapperImport::FindSettings", "Found " + source + " settings at " + newest);
		else
			App.Logger.WriteLine("BootstrapperImport::FindSettings", "No " + source + " settings were found in Wine, Proton, Bottles, Lutris or mounted Windows drives");
		return newest;
	}

	public static bool NeedsHostRead(string path)
	{
		return LinuxFlatpakHost.IsSandboxed && !File.Exists(path);
	}

	public static async Task<string?> ReadHostTextAsync(string path, long maximumBytes, CancellationToken token)
	{
		long limit = Math.Min(maximumBytes, HostReadLimit);
		ProcessExecution? result = await RunHostAsync("head", ["-c", (limit + 1).ToString(CultureInfo.InvariantCulture), "--", path], token).ConfigureAwait(false);
		if (result is null || result.ExitCode != 0)
			return null;
		if (result.StandardOutput.Length > limit)
			throw new InvalidDataException(Path.GetFileName(path) + " is too large to import");
		return result.StandardOutput;
	}

	public static async Task<string> ResolvePickedPathAsync(string path)
	{
#if CROSSPLAT
		if (!LinuxFlatpakHost.IsSandboxed || Directory.Exists(Path.Combine(Path.GetDirectoryName(path) ?? "/", "Modifications")))
			return path;

		string? documentId = DocumentId(path);
		if (documentId is null)
			return path;

		try
		{
			using Connection connection = new(LinuxSessionBus.RequireAddress());
			await connection.ConnectAsync().ConfigureAwait(false);
			IDocumentsPortal documents = connection.CreateProxy<IDocumentsPortal>("org.freedesktop.portal.Documents", new ObjectPath("/org/freedesktop/portal/documents"));
			IDictionary<string, byte[]> hostPaths = await documents.GetHostPathsAsync([documentId]).ConfigureAwait(false);
			if (hostPaths.TryGetValue(documentId, out byte[]? bytes) && bytes is { Length: > 0 })
			{
				string hostPath = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
				if (Path.IsPathRooted(hostPath))
				{
					App.Logger.WriteLine("BootstrapperImport::ResolvePickedPath", "The picked file is " + hostPath + " on the host");
					return hostPath;
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("BootstrapperImport::ResolvePickedPath", "The picked file stays sandboxed, FastFlags next to it cannot be read: " + ex.GetBaseException().Message);
		}
#endif
		return path;
	}

	private static string? DocumentId(string path)
	{
		string full = Path.GetFullPath(path);
		foreach (string root in new[] { Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "", "/run/flatpak" })
		{
			if (string.IsNullOrEmpty(root))
				continue;
			string prefix = root.TrimEnd('/') + "/doc/";
			if (!full.StartsWith(prefix, StringComparison.Ordinal))
				continue;
			string rest = full[prefix.Length..];
			int slash = rest.IndexOf('/');
			string id = slash < 0 ? rest : rest[..slash];
			return id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit) ? id : null;
		}
		return null;
	}

	private static async Task<ProcessExecution?> RunHostAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
	{
		Voidstrap.Core.SystemProcessService processes = new();
		if (!LinuxFlatpakHost.TryCreateHostCommand(processes, fileName, arguments, out ProcessCommand command))
			return null;
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, token).ConfigureAwait(false);
		token.ThrowIfCancellationRequested();
		return result.Succeeded ? result.Value : null;
	}

	private static string ToShellWord(string pattern)
	{
		return string.Join("/", pattern.Split('/').Select(segment => segment switch
		{
			"" => "",
			"~" => "\"$HOME\"",
			"*" => "*",
			_ when segment.StartsWith('$') => "\"${" + segment[1..] + ":-/nonexistent}\"",
			_ => "'" + segment.Replace("'", "'\\''") + "'"
		}));
	}
}

#if CROSSPLAT
[DBusInterface("org.freedesktop.portal.Documents")]
public interface IDocumentsPortal : IDBusObject
{
	Task<IDictionary<string, byte[]>> GetHostPathsAsync(string[] documentIds);
}
#endif
