using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Voidstrap.AppData;
using AppSettings = Voidstrap.Models.Persistable.AppSettings;
using Voidstrap.Models.Persistable;

namespace Voidstrap;

internal static partial class Cli
{
	public const string Verb = "cli";

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

	public static void AttachTerminal()
	{
		if (!OperatingSystem.IsWindows() || !AttachConsole(-1))
			return;
		Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
		Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
		Console.WriteLine();
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
				Console.WriteLine(Usage);
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
			case "uninstall":
				return await RunSelfAsync(true, "-uninstall");
			default:
				Console.Error.WriteLine("Unknown command: " + command);
				Console.Error.WriteLine(Usage);
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

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AttachConsole(int processId);
}
