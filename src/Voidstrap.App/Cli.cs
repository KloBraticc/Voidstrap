using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Voidstrap.AppData;
using AppSettings = Voidstrap.Models.Persistable.AppSettings;
using Voidstrap.Models.Persistable;

namespace Voidstrap;

internal static partial class Cli
{
	public const string Verb = "cli";

	private const int StdInputHandle = -10;
	private const int StdOutputHandle = -11;
	private const int StdErrorHandle = -12;
	private const uint FileTypeDisk = 1;
	private const uint FileTypePipe = 3;
	private const uint GenericWrite = 0x40000000;
	private const uint FileShareReadWrite = 3;
	private const uint OpenExisting = 3;

	private const string Usage = """
		Usage: Voidstrap cli <command>

		  status                      Show Voidstrap and Roblox details
		  version                     Show the Voidstrap version
		  launch                      Launch Roblox
		  join <placeId> [jobId]      Join a place, or a specific server
		  studio                      Launch Roblox Studio
		  install                     Install or update Roblox without launching it
		  update                      Same as install
		  repair                      Download Roblox again from scratch
		  cache clear                 Clear the Roblox cache
		  channel [name]              Show or change the Roblox channel
		  flags list                  List FastFlags
		  flags get <name>            Show one FastFlag
		  flags set <name> <value>    Set a FastFlag
		  flags remove <name>         Remove a FastFlag
		  flags import <file>         Merge FastFlags from a JSON file
		  flags export <file>         Write FastFlags to a JSON file
		  settings list               List settings
		  settings get <name>         Show one setting
		  settings set <name> <value> Change a setting
		  uninstall                   Uninstall Voidstrap
		""";

	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	public static bool IsCliInvocation(string[] args) =>
		args.Length > 0 && args[0].Equals(Verb, StringComparison.OrdinalIgnoreCase);

	internal static bool IsStdInputRedirected()
	{
		if (!OperatingSystem.IsWindows())
			return Console.IsInputRedirected;
		return IsStdHandleRedirected(StdInputHandle);
	}

	internal static bool IsStdOutputRedirected()
	{
		if (!OperatingSystem.IsWindows())
			return Console.IsOutputRedirected;
		return IsStdHandleRedirected(StdOutputHandle);
	}

	internal static bool IsStdErrorRedirected()
	{
		if (!OperatingSystem.IsWindows())
			return Console.IsErrorRedirected;
		return IsStdHandleRedirected(StdErrorHandle);
	}

	internal static bool IsMcpStdioPipeActive()
	{
		if (!OperatingSystem.IsWindows())
			return false;
		if (!IsStdInputRedirected() || !IsStdOutputRedirected())
			return false;
		nint handle = GetStdHandle(StdInputHandle);
		if (handle == 0 || handle == -1)
			return false;
		uint fileType = GetFileType(handle);
		return fileType is FileTypeDisk or FileTypePipe;
	}

	private static bool IsStdHandleRedirected(int stdHandle)
	{
		nint handle = GetStdHandle(stdHandle);
		if (handle == 0 || handle == -1)
			return false;
		uint fileType = GetFileType(handle);
		return fileType is FileTypeDisk or FileTypePipe;
	}

	private static Stream OpenConsoleOutStream(bool isError)
	{
		nint handle = CreateFileW("CONOUT$", GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
		if (handle != 0 && handle != -1)
			return new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Write);
		return isError ? Console.OpenStandardError() : Console.OpenStandardOutput();
	}

	public static void AttachTerminal()
	{
		if (!OperatingSystem.IsWindows())
			return;
		bool outRedirected = IsStdOutputRedirected();
		bool errRedirected = IsStdErrorRedirected();
		Stream? pipedOut = outRedirected ? Console.OpenStandardOutput() : null;
		Stream? pipedErr = errRedirected ? Console.OpenStandardError() : null;
		if (outRedirected && errRedirected)
		{
			if (pipedOut != null)
				Console.SetOut(new StreamWriter(pipedOut) { AutoFlush = true });
			if (pipedErr != null)
				Console.SetError(new StreamWriter(pipedErr) { AutoFlush = true });
			return;
		}
		bool attached = AttachConsole(-1) || GetConsoleWindow() != 0;
		if (pipedOut != null)
		{
			Console.SetOut(new StreamWriter(pipedOut) { AutoFlush = true });
		}
		else if (attached)
		{
			Console.SetOut(new StreamWriter(OpenConsoleOutStream(false)) { AutoFlush = true });
			Console.WriteLine();
		}
		if (pipedErr != null)
		{
			Console.SetError(new StreamWriter(pipedErr) { AutoFlush = true });
		}
		else if (attached)
		{
			Console.SetError(new StreamWriter(OpenConsoleOutStream(true)) { AutoFlush = true });
		}
	}

	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			return await DispatchAsync(args);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("Error: " + ex.Message);
			return 1;
		}
	}

	private static async Task<int> DispatchAsync(string[] args)
	{
		string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
		string[] rest = args.Skip(1).ToArray();
		switch (command)
		{
			case "help":
			case "--help":
			case "-h":
				Console.WriteLine(GetUsageText());
				return 0;
			case "version":
				Console.WriteLine(App.Version);
				return 0;
			case "status":
				return Status();
			case "launch":
				return await RunSelfAsync(false, "-player");
			case "join":
				return await JoinAsync(rest);
			case "studio":
				return await RunSelfAsync(false, "-studio");
			case "install":
			case "update":
				return await InstallAsync(false);
			case "repair":
				return await InstallAsync(true);
			case "cache":
				return ClearCache(rest);
			case "channel":
				return Channel(rest);
			case "flags":
				return Flags(rest);
			case "settings":
				return Settings(rest);
			case "mcp":
			case "-mcp":
			case "--mcp":
			case "/mcp":
				return Mcp(rest);
			case "uninstall":
				return await RunSelfAsync(true, "-uninstall");
			default:
				if (IsAiClientShortcutArg(command))
					return Mcp(args);
				Console.Error.WriteLine("Unknown command: " + command);
				Console.Error.WriteLine(GetUsageText());
				return 2;
		}
	}

	private static int Status()
	{
		Console.WriteLine("Voidstrap: " + App.Version);
		Console.WriteLine("Channel: " + App.Settings.Prop.Channel);
		Console.WriteLine("FastFlags: " + App.FastFlags.Prop.Count);
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			Console.WriteLine("Roblox: managed by Sober");
			return 0;
		}
		string version = new RobloxPlayerData().State.VersionGuid;
		Console.WriteLine("Roblox: " + (string.IsNullOrEmpty(version) ? "not installed" : version));
		Process[] running = Process.GetProcessesByName(Voidstrap.Utility.Platform.RobloxPlayerProcessName);
		Console.WriteLine("Running: " + running.Length);
		foreach (Process process in running)
			process.Dispose();
		return 0;
	}

	private static Task<int> JoinAsync(string[] args)
	{
		if (args.Length < 1 || !long.TryParse(args[0], out long placeId) || placeId <= 0)
		{
			Console.Error.WriteLine("Usage: Voidstrap cli join <placeId> [jobId]");
			return Task.FromResult(2);
		}
		string link = "roblox://experiences/start?placeId=" + placeId;
		if (args.Length > 1)
		{
			if (!Guid.TryParse(args[1], out Guid jobId))
			{
				Console.Error.WriteLine("The job id is not valid");
				return Task.FromResult(2);
			}
			link += "&gameInstanceId=" + jobId;
		}
		return RunSelfAsync(false, "-player", link);
	}

	private static async Task<int> InstallAsync(bool repair)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			Console.Error.WriteLine("Sober installs and updates Roblox on Linux");
			return 1;
		}
		if (repair)
		{
			RobloxPlayerData player = new();
			player.State.VersionGuid = string.Empty;
			App.State.Save();
			Console.WriteLine("Roblox will be downloaded again");
		}
		return await RunSelfAsync(true, "-player", "-nolaunch");
	}

	private static int ClearCache(string[] args)
	{
		if (args.Length != 1 || !args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
		{
			Console.Error.WriteLine("Usage: Voidstrap cli cache clear");
			return 2;
		}
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			Console.Error.WriteLine("Clear the cache from Sober on Linux");
			return 1;
		}
		if (Voidstrap.Utility.ProcessScan.Any(Voidstrap.Utility.Platform.RobloxPlayerProcessName))
		{
			Console.Error.WriteLine("Close Roblox first");
			return 1;
		}
		if (!Directory.Exists(Paths.RobloxCache))
		{
			Console.WriteLine("The Roblox cache is already empty");
			return 0;
		}
		int failed = 0;
		foreach (string entry in Directory.EnumerateFileSystemEntries(Paths.RobloxCache))
		{
			try
			{
				if (Directory.Exists(entry))
					Directory.Delete(entry, true);
				else
					File.Delete(entry);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				failed++;
			}
		}
		Console.WriteLine(failed == 0 ? "The Roblox cache was cleared" : $"The Roblox cache was cleared, {failed} items were in use");
		return 0;
	}

	private static int Channel(string[] args)
	{
		if (args.Length == 0)
		{
			Console.WriteLine(App.Settings.Prop.Channel);
			return 0;
		}
		App.Settings.Prop.Channel = args[0];
		App.Settings.Save();
		Console.WriteLine("Channel set to " + args[0] + ", run install to switch Roblox to it");
		return 0;
	}

	private static int Flags(string[] args)
	{
		string action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
		switch (action)
		{
			case "list":
				foreach (KeyValuePair<string, object> flag in App.FastFlags.Prop.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
					Console.WriteLine(flag.Key + " = " + flag.Value);
				return 0;
			case "get" when args.Length == 2:
				string? value = App.FastFlags.GetValue(args[1]);
				if (value == null)
				{
					Console.Error.WriteLine(args[1] + " is not set");
					return 1;
				}
				Console.WriteLine(value);
				return 0;
			case "set" when args.Length == 3:
				App.FastFlags.SetValue(args[1], args[2]);
				App.FastFlags.Save();
				Console.WriteLine(args[1] + " = " + args[2]);
				return 0;
			case "remove" when args.Length == 2:
				if (App.FastFlags.GetValue(args[1]) == null)
				{
					Console.Error.WriteLine(args[1] + " is not set");
					return 1;
				}
				App.FastFlags.SetValue(args[1], null);
				App.FastFlags.Save();
				Console.WriteLine("Removed " + args[1]);
				return 0;
			case "import" when args.Length == 2:
				Dictionary<string, JsonElement> imported = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(args[1]))
					?? throw new InvalidDataException("The file does not hold a JSON object");
				foreach (KeyValuePair<string, JsonElement> flag in imported)
					App.FastFlags.SetValue(flag.Key, flag.Value.ValueKind == JsonValueKind.String ? flag.Value.GetString() : flag.Value.GetRawText());
				App.FastFlags.Save();
				Console.WriteLine($"Imported {imported.Count} FastFlags");
				return 0;
			case "export" when args.Length == 2:
				File.WriteAllText(args[1], JsonSerializer.Serialize(App.FastFlags.Prop, Indented));
				Console.WriteLine($"Exported {App.FastFlags.Prop.Count} FastFlags to {args[1]}");
				return 0;
			default:
				Console.Error.WriteLine("Usage: Voidstrap cli flags list|get|set|remove|import|export");
				return 2;
		}
	}

	private static int Settings(string[] args)
	{
		string action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
		AppSettings settings = App.Settings.Prop;
		switch (action)
		{
			case "list":
				foreach (PropertyInfo property in EditableSettings())
					Console.WriteLine(property.Name + " = " + property.GetValue(settings));
				return 0;
			case "get" when args.Length == 2:
			{
				PropertyInfo? property = FindSetting(args[1]);
				if (property == null)
					return UnknownSetting(args[1]);
				Console.WriteLine(property.GetValue(settings));
				return 0;
			}
			case "set" when args.Length == 3:
			{
				PropertyInfo? property = FindSetting(args[1]);
				if (property == null)
					return UnknownSetting(args[1]);
				Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
				object value = type.IsEnum
					? Enum.Parse(type, args[2], true)
					: Convert.ChangeType(args[2], type, System.Globalization.CultureInfo.InvariantCulture);
				property.SetValue(settings, value);
				App.Settings.Save();
				Console.WriteLine(property.Name + " = " + value);
				return 0;
			}
			default:
				Console.Error.WriteLine("Usage: Voidstrap cli settings list|get|set");
				return 2;
		}
	}

	private static IEnumerable<PropertyInfo> EditableSettings() =>
		typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && IsSimple(p.PropertyType))
			.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

	private static bool IsSimple(Type type)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
	}

	private static PropertyInfo? FindSetting(string name) =>
		EditableSettings().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

	private static int UnknownSetting(string name)
	{
		Console.Error.WriteLine("Unknown setting: " + name);
		return 1;
	}

	private static async Task<int> RunSelfAsync(bool wait, params string[] arguments)
	{
		ProcessStartInfo startInfo = new(Paths.Process) { UseShellExecute = false };
		foreach (string argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Voidstrap did not start");
		if (!wait)
		{
			Console.WriteLine("Started");
			return 0;
		}
		Console.WriteLine("Working, follow the Voidstrap window for progress");
		await process.WaitForExitAsync();
		Console.WriteLine(process.ExitCode == 0 ? "Done" : "Finished with exit code " + process.ExitCode);
		return process.ExitCode;
	}

	private static string GetUsageText()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return Usage;

		string dLabel = Voidstrap.Integrations.Mcp.McpConnectInfo.DesktopClientLabel;
		string cLabel = Voidstrap.Integrations.Mcp.McpConnectInfo.CliClientLabel;
		string oLabel = Voidstrap.Integrations.Mcp.McpConnectInfo.OpenCodeClientLabel;

		return Usage + Environment.NewLine +
			$"  mcp                         Auto connect MCP to {dLabel}, {cLabel}, and {oLabel}" + Environment.NewLine +
			$"  mcp +{dLabel.ToLowerInvariant(),-22} Auto connect MCP to {dLabel}" + Environment.NewLine +
			$"  mcp +{cLabel.ToLowerInvariant(),-22} Auto connect MCP to {cLabel}" + Environment.NewLine +
			$"  mcp +{oLabel.ToLowerInvariant(),-22} Auto connect MCP to {oLabel}" + Environment.NewLine +
			"  mcp status                  Show MCP connection status";
	}

	public static bool TryRunMcpShortcut(string[] fullCommandLineArgs, out int exitCode)
	{
		exitCode = 0;
		if (!Voidstrap.Utility.Platform.IsWindows || fullCommandLineArgs.Length < 2)
			return false;

		string[] userArgs = fullCommandLineArgs[1..];
		string[]? mcpArgs = null;

		if (Voidstrap.Integrations.Mcp.McpStdioServer.IsMcpToken(userArgs[0]))
		{
			mcpArgs = userArgs[1..];
		}
		else if (userArgs.Length >= 2 && userArgs[0].Equals(Verb, StringComparison.OrdinalIgnoreCase) && Voidstrap.Integrations.Mcp.McpStdioServer.IsMcpToken(userArgs[1]))
		{
			mcpArgs = userArgs[2..];
		}
		else if (userArgs.All(IsAiClientShortcutArg))
		{
			mcpArgs = userArgs;
		}
		else if (userArgs.Length >= 2 && userArgs[0].Equals(Verb, StringComparison.OrdinalIgnoreCase) && userArgs.Skip(1).All(IsAiClientShortcutArg))
		{
			mcpArgs = userArgs[1..];
		}

		if (mcpArgs == null)
			return false;

		AttachTerminal();
		exitCode = Mcp(mcpArgs);
		return true;
	}

	private static bool IsAiClientShortcutArg(string arg)
	{
		if (string.IsNullOrWhiteSpace(arg))
			return false;

		string trimmed = arg.Trim();
		if (!trimmed.StartsWith('+') && !trimmed.StartsWith('-') && !trimmed.StartsWith('/'))
			return false;

		string clean = trimmed.TrimStart('+', '-', '/').Replace("-", string.Empty);
		return string.Equals(clean, Voidstrap.Integrations.Mcp.McpConnectInfo.DesktopClientLabel, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(clean, Voidstrap.Integrations.Mcp.McpConnectInfo.CliClientLabel, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(clean, Voidstrap.Integrations.Mcp.McpConnectInfo.OpenCodeClientLabel, StringComparison.OrdinalIgnoreCase);
	}

	private static int Mcp(string[] args)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			Console.Error.WriteLine("MCP is only available on Windows");
			return 1;
		}

		string dLabel = Voidstrap.Integrations.Mcp.McpConnectInfo.DesktopClientLabel;
		string cLabel = Voidstrap.Integrations.Mcp.McpConnectInfo.CliClientLabel;
		string oLabel = Voidstrap.Integrations.Mcp.McpConnectInfo.OpenCodeClientLabel;

		if (args.Any(a => a.Equals("help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase) || a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("/?", StringComparison.OrdinalIgnoreCase)))
		{
			Console.WriteLine($"Usage: Voidstrap mcp [+{dLabel.ToLowerInvariant()}] [+{cLabel.ToLowerInvariant()}] [+{oLabel.ToLowerInvariant()}] [status]");
			Console.WriteLine($"  Voidstrap mcp             Auto connect MCP to {dLabel}, {cLabel}, and {oLabel}");
			Console.WriteLine($"  Voidstrap mcp +{dLabel.ToLowerInvariant(),-10} Auto connect MCP to {dLabel}");
			Console.WriteLine($"  Voidstrap mcp +{cLabel.ToLowerInvariant(),-10} Auto connect MCP to {cLabel}");
			Console.WriteLine($"  Voidstrap mcp +{oLabel.ToLowerInvariant(),-10} Auto connect MCP to {oLabel}");
			Console.WriteLine("  Voidstrap mcp status      Show MCP connection status");
			return 0;
		}

		if (args.Length == 1 && args[0].TrimStart('+', '-', '/').ToLowerInvariant() is "status" or "list")
		{
			Voidstrap.Integrations.Mcp.McpConnectInfo.RefreshConnectedClients();
			Console.WriteLine("Voidstrap MCP Command: " + Voidstrap.Integrations.Mcp.McpConnectInfo.ExecutablePath() + " " + Voidstrap.Integrations.Mcp.McpStdioServer.CommandArg);
			Console.WriteLine(dLabel + ": " + (Voidstrap.Integrations.Mcp.McpConnectInfo.IsDesktopClientConnected() ? "connected" : "not connected"));
			Console.WriteLine(cLabel + ": " + (Voidstrap.Integrations.Mcp.McpConnectInfo.IsCliClientConnected() ? "connected" : "not connected"));
			Console.WriteLine(oLabel + ": " + (Voidstrap.Integrations.Mcp.McpConnectInfo.IsOpenCodeClientConnected() ? "connected" : "not connected"));
			return 0;
		}

		bool connectDesktop = false;
		bool connectCli = false;
		bool connectOpenCode = false;

		if (args.Length == 0)
		{
			connectDesktop = true;
			connectCli = true;
			connectOpenCode = true;
		}
		else
		{
			foreach (string raw in args)
			{
				string token = raw.Trim().TrimStart('+', '-', '/').Replace("-", string.Empty);
				if (string.Equals(token, dLabel, StringComparison.OrdinalIgnoreCase))
				{
					connectDesktop = true;
				}
				else if (string.Equals(token, cLabel, StringComparison.OrdinalIgnoreCase))
				{
					connectCli = true;
				}
				else if (string.Equals(token, oLabel, StringComparison.OrdinalIgnoreCase))
				{
					connectOpenCode = true;
				}
				else if (string.Equals(token, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "connect", StringComparison.OrdinalIgnoreCase))
				{
					connectDesktop = true;
					connectCli = true;
					connectOpenCode = true;
				}
				else
				{
					Console.Error.WriteLine("Unknown MCP target: " + raw);
					Console.Error.WriteLine($"Usage: Voidstrap mcp [+{dLabel.ToLowerInvariant()}] [+{cLabel.ToLowerInvariant()}] [+{oLabel.ToLowerInvariant()}] [status]");
					return 2;
				}
			}
		}

		int failures = 0;

		if (connectDesktop)
		{
			bool ok = Voidstrap.Integrations.Mcp.McpConnectInfo.ConnectDesktopClient();
			if (!ok)
				failures++;
			Console.WriteLine(dLabel + ": " + (ok ? "connected" : "failed"));
		}

		if (connectCli)
		{
			bool ok = Voidstrap.Integrations.Mcp.McpConnectInfo.ConnectCliClient();
			if (!ok)
				failures++;
			Console.WriteLine(cLabel + ": " + (ok ? "connected" : "failed"));
		}

		if (connectOpenCode)
		{
			bool ok = Voidstrap.Integrations.Mcp.McpConnectInfo.ConnectOpenCodeClient();
			if (!ok)
				failures++;
			Console.WriteLine(oLabel + ": " + (ok ? "connected" : "failed"));
		}

		return failures == 0 ? 0 : 1;
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AttachConsole(int processId);

	[LibraryImport("kernel32.dll")]
	private static partial nint GetConsoleWindow();

	[LibraryImport("kernel32.dll")]
	private static partial nint GetStdHandle(int nStdHandle);

	[LibraryImport("kernel32.dll")]
	private static partial uint GetFileType(nint hFile);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool PeekNamedPipe(
		nint hNamedPipe,
		nint lpBuffer,
		uint nBufferSize,
		nint lpBytesRead,
		out uint lpTotalBytesAvail,
		nint lpBytesLeftThisMessage);

	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial nint CreateFileW(
		string lpFileName,
		uint dwDesiredAccess,
		uint dwShareMode,
		nint lpSecurityAttributes,
		uint dwCreationDisposition,
		uint dwFlagsAndAttributes,
		nint hTemplateFile);
}
