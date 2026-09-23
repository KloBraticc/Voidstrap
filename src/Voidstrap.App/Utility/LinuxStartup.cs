using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static partial class LinuxStartup
{
	private const string ConfiguredFlag = "VOIDSTRAP_GL_CONFIGURED";

	private const string ForceGpuFlag = "VOIDSTRAP_USE_GPU";

	private const string GpuRetryFlag = "VOIDSTRAP_GPU_RETRY";

	private const string SupervisedFlag = "VOIDSTRAP_RENDER_SUPERVISED";

	private const string OwnedKeysFlag = "VOIDSTRAP_RENDER_OWNED";

	private const string DefaultStage = "default";

	private const string HardwareGlStage = "gl";

	private const string SoftwareStage = "software";

	private const string ApplicationName = "Voidstrap";

	private const string MissingVulkanDriver = "/nonexistent/voidstrap-no-vulkan.json";

	private const int RendererFailedExitCode = 86;

	private static readonly int SoftwareThreadCount = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 8);

	private static bool _subscribed;

	private static bool _supervised;

	private static string _activeStage = DefaultStage;

	private static int _probeStarted;

	private static int _confirmed;

	public static string ActiveStage => _activeStage;

	[ModuleInitializer]
	internal static void Initialize()
	{
		AppDomain.CurrentDomain.UnhandledException += OnFatalException;
		_subscribed = true;
		if (OperatingSystem.IsMacOS())
		{
			TextFontInstaller.Install();
		}
		if (!OperatingSystem.IsLinux())
		{
			return;
		}
		if (IsConfiguredForThisProcess())
		{
			_activeStage = NormaliseStage(Environment.GetEnvironmentVariable(GpuRetryFlag));
			_supervised = Environment.GetEnvironmentVariable(SupervisedFlag) == "1";
			RestoreChildEnvironment();
			return;
		}
		TextFontInstaller.Install();
		string stage = ChooseStage(out bool confirmed);
		string? executable = Environment.ProcessPath;
		if (!string.IsNullOrEmpty(executable))
		{
			if (confirmed || Environment.GetEnvironmentVariable(ForceGpuFlag) == "1")
			{
				ReplaceProcess(CreateStartInfo(executable, CurrentArguments(), stage, "pid:" + Environment.ProcessId));
			}
			else
			{
				Supervise(executable, stage);
			}
		}
		_activeStage = stage;
		foreach (KeyValuePair<string, string?> entry in CreateStartInfo(executable ?? ApplicationName, [], stage, "pid:" + Environment.ProcessId).Environment)
		{
			Environment.SetEnvironmentVariable(entry.Key, entry.Value);
		}
	}

	internal static readonly string[] BackendNames = ["Auto", "Vulkan", "OpenGL", "Software"];

	private static bool IsConfiguredForThisProcess()
	{
		string? value = Environment.GetEnvironmentVariable(ConfiguredFlag);
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}
		if (value.StartsWith("pid:", StringComparison.Ordinal))
		{
			return value[4..] == Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
		}
		if (value.StartsWith("ppid:", StringComparison.Ordinal))
		{
			return value[5..] == GetParentProcessId().ToString(CultureInfo.InvariantCulture);
		}
		return false;
	}

	private static void RestoreChildEnvironment()
	{
		foreach (string key in (Environment.GetEnvironmentVariable(OwnedKeysFlag) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			Environment.SetEnvironmentVariable(key, null);
		}
		string? libraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
		if (libraryPath != null)
		{
			string? blockDirectory = EglBlockDirectory();
			string stripped = string.Join(':', libraryPath.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(entry => entry != blockDirectory));
			Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", stripped.Length == 0 ? null : stripped);
		}
		foreach (string key in new[] { ConfiguredFlag, SupervisedFlag, OwnedKeysFlag })
		{
			Environment.SetEnvironmentVariable(key, null);
		}
	}

	private static string ChooseStage(out bool confirmed)
	{
		ReadRendererMarker(out string recorded, out bool markerConfirmed, out _);
		string? requested = Environment.GetEnvironmentVariable(GpuRetryFlag);
		string stage = !string.IsNullOrEmpty(requested)
			? NormaliseStage(requested)
			: Environment.GetEnvironmentVariable(ForceGpuFlag) == "1" || recorded.Length == 0 ? DefaultStage : recorded;
		confirmed = markerConfirmed && recorded == stage;
		return stage;
	}

	private static string[] CurrentArguments()
	{
		return Environment.GetCommandLineArgs().Skip(1).ToArray();
	}

	private static void Supervise(string executable, string stage)
	{
		for (string current = stage; ; current = NextStage(current))
		{
			int exitCode;
			try
			{
				ProcessStartInfo startInfo = CreateStartInfo(executable, CurrentArguments(), current, "ppid:" + Environment.ProcessId);
				startInfo.Environment[SupervisedFlag] = "1";
				using Process? child = Process.Start(startInfo);
				if (child == null)
				{
					return;
				}
				child.WaitForExit();
				exitCode = child.ExitCode;
			}
			catch (Exception)
			{
				return;
			}
			ReadRendererMarker(out string recorded, out bool confirmed, out _);
			bool rendererFailed = !confirmed && recorded == current && exitCode != 0;
			if (!rendererFailed || current == SoftwareStage)
			{
				if (rendererFailed)
				{
					WriteError("Voidstrap could not start a GPU or software renderer on this system. Install your graphics drivers, or the Mesa Vulkan drivers package for software rendering, and try again.");
				}
				Environment.Exit(exitCode);
			}
			WriteError(NextStage(current) == HardwareGlStage
				? "Voidstrap could not start the Vulkan renderer, retrying with OpenGL on your graphics card."
				: "Voidstrap could not start an accelerated renderer, retrying with software rendering. Expect reduced performance until your graphics drivers are updated.");
		}
	}

	private static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments, string stage, string configuredFor)
	{
		ProcessStartInfo startInfo = new(executable)
		{
			UseShellExecute = false
		};
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}
		IDictionary<string, string?> environment = startInfo.Environment;
		foreach (string key in (environment.TryGetValue(OwnedKeysFlag, out string? owned) ? owned ?? "" : "").Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			environment.Remove(key);
		}
		environment.Remove(SupervisedFlag);
		string? libraryPath = environment.TryGetValue("LD_LIBRARY_PATH", out string? currentPath) ? currentPath : null;
		string? blockDirectory = EglBlockDirectory();
		libraryPath = string.Join(':', (libraryPath ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Where(entry => entry != blockDirectory));
		List<string> ownedKeys = [];
		void Set(string key, string value)
		{
			if (!environment.ContainsKey(key))
			{
				ownedKeys.Add(key);
			}
			environment[key] = value;
		}
		environment[ConfiguredFlag] = configuredFor;
		environment[GpuRetryFlag] = stage;
		Set("RESOURCE_NAME", ApplicationName);
		Set("SDL_VIDEO_X11_WMCLASS", ApplicationName);
		string backend = stage switch
		{
			HardwareGlStage => "OpenGL",
			SoftwareStage => "Software",
			_ => ReadConfiguredBackend()
		};
		environment["VOIDSTRAP_RENDER_BACKEND"] = backend;
		string[] lavapipe = LavapipeDrivers();
		bool softwareGl = backend == "Software" && lavapipe.Length == 0;
		if (backend == "OpenGL" || softwareGl)
		{
			Set("VK_DRIVER_FILES", MissingVulkanDriver);
			Set("VK_ICD_FILENAMES", MissingVulkanDriver);
			if (softwareGl)
			{
				Set("LIBGL_ALWAYS_SOFTWARE", "1");
				Set("LP_NUM_THREADS", SoftwareThreadCount.ToString(CultureInfo.InvariantCulture));
			}
		}
		else
		{
			string? blocked = PrepareEglBlock();
			if (blocked != null)
			{
				libraryPath = libraryPath.Length == 0 ? blocked : blocked + ":" + libraryPath;
			}
			if (backend == "Software")
			{
				Set("VK_DRIVER_FILES", string.Join(':', lavapipe));
				Set("VK_ICD_FILENAMES", string.Join(':', lavapipe));
				Set("LP_NUM_THREADS", SoftwareThreadCount.ToString(CultureInfo.InvariantCulture));
			}
		}
		if (libraryPath.Length == 0)
		{
			environment.Remove("LD_LIBRARY_PATH");
		}
		else
		{
			environment["LD_LIBRARY_PATH"] = libraryPath;
		}
		environment[OwnedKeysFlag] = string.Join(',', ownedKeys);
		return startInfo;
	}

	private static string CacheDirectory
	{
		get
		{
			string cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? string.Empty;
			if (string.IsNullOrWhiteSpace(cache))
			{
				cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
			}
			return Path.Combine(cache, "voidstrap");
		}
	}

	private static string? EglBlockDirectory()
	{
		try
		{
			return Path.Combine(CacheDirectory, "egl-off");
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string? PrepareEglBlock()
	{
		try
		{
			string? directory = EglBlockDirectory();
			string? library = LoadedLibraryWithoutEgl();
			if (directory == null || library == null)
			{
				return null;
			}
			Directory.CreateDirectory(directory);
			string link = Path.Combine(directory, "libEGL.so.1");
			if (!string.Equals(new FileInfo(link).LinkTarget, library, StringComparison.Ordinal))
			{
				File.Delete(link);
				File.CreateSymbolicLink(link, library);
			}
			return directory;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string? LoadedLibraryWithoutEgl()
	{
		string[] mapped = File.ReadAllLines("/proc/self/maps")
			.Select(line => line.IndexOf('/') is int start and >= 0 ? line[start..] : string.Empty)
			.Where(path => path.Length > 0)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		foreach (string name in new[] { "/libm.so.6", "/libc.so.6" })
		{
			string? match = mapped.FirstOrDefault(path => path.EndsWith(name, StringComparison.Ordinal));
			if (match != null)
			{
				return match;
			}
		}
		return mapped.FirstOrDefault(path => Path.GetFileName(path).StartsWith("libc.musl", StringComparison.Ordinal)
			|| Path.GetFileName(path).StartsWith("ld-musl", StringComparison.Ordinal));
	}

	private static string[] LavapipeDrivers()
	{
		List<string> drivers = [];
		foreach (string directory in new[] { "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d", "/usr/local/share/vulkan/icd.d" })
		{
			try
			{
				if (Directory.Exists(directory))
				{
					drivers.AddRange(Directory.EnumerateFiles(directory, "lvp_icd*.json"));
				}
			}
			catch (Exception)
			{
			}
		}
		return [.. drivers];
	}

	private static string SettingsPath
	{
		get
		{
			string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? string.Empty;
			if (string.IsNullOrWhiteSpace(config))
			{
				config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
			}

			return Path.Combine(config, "voidstrap", "AppSettings.json");
		}
	}

	private static string ReadConfiguredBackend()
	{
		try
		{
			string path = SettingsPath;
			if (!File.Exists(path))
			{
				return "Auto";
			}

			using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
			if (document.RootElement.TryGetProperty("LinuxRenderBackend", out System.Text.Json.JsonElement value)
				&& value.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				string selected = value.GetString() ?? "Auto";
				foreach (string name in BackendNames)
				{
					if (string.Equals(name, selected, StringComparison.OrdinalIgnoreCase))
						return name;
				}
			}
		}
		catch (Exception)
		{
		}

		return "Auto";
	}

	private static string RendererMarkerPath
	{
		get
		{
			string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? string.Empty;
			if (string.IsNullOrWhiteSpace(state))
			{
				state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
			}

			return Path.Combine(state, "voidstrap", "renderer-stage");
		}
	}

	private static string NextStage(string stage)
	{
		return stage switch
		{
			DefaultStage => HardwareGlStage,
			HardwareGlStage => SoftwareStage,
			_ => SoftwareStage
		};
	}

	private static string NormaliseStage(string? stage)
	{
		return stage switch
		{
			HardwareGlStage => HardwareGlStage,
			SoftwareStage => SoftwareStage,
			_ => DefaultStage
		};
	}

	private static void ReadRendererMarker(out string stage, out bool confirmed, out int attempts)
	{
		stage = string.Empty;
		confirmed = false;
		attempts = 0;
		try
		{
			string path = RendererMarkerPath;
			if (!File.Exists(path))
			{
				return;
			}

			string[] parts = File.ReadAllText(path).Trim().Split('|');
			if (parts.Length == 0)
			{
				return;
			}

			stage = NormaliseStage(parts[0].Trim());
			if (parts.Length > 1)
			{
				confirmed = string.Equals(parts[1].Trim(), "ok", StringComparison.Ordinal);
			}

			if (parts.Length > 2 && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
			{
				attempts = parsed;
			}
		}
		catch (Exception)
		{
			stage = string.Empty;
			confirmed = false;
			attempts = 0;
		}
	}

	private static void WriteRendererMarker(string stage, bool confirmed, int attempts)
	{
		try
		{
			string path = RendererMarkerPath;
			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllText(path, stage + "|" + (confirmed ? "ok" : "pending") + "|" + attempts.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception)
		{
		}
	}

	public static void BeginRendererProbe()
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}
		if (Interlocked.Exchange(ref _probeStarted, 1) != 0)
		{
			return;
		}

		ReadRendererMarker(out string recordedStage, out bool confirmed, out int attempts);
		int nextAttempt = !confirmed && recordedStage == _activeStage ? attempts + 1 : 1;
		WriteRendererMarker(_activeStage, false, nextAttempt);
	}

	public static bool MarkRendererHealthy(bool requireRenderer)
	{
		if (!OperatingSystem.IsLinux() || Volatile.Read(ref _probeStarted) == 0)
		{
			return true;
		}
		if (Volatile.Read(ref _confirmed) != 0)
		{
			return true;
		}
#if CROSSPLAT
		if (requireRenderer && !ProGPU.Backend.WgpuContext.TryGetFirstActiveContext(out _))
		{
			return false;
		}
#endif
		if (Interlocked.Exchange(ref _confirmed, 1) == 0)
		{
			WriteRendererMarker(_activeStage, true, 0);
		}
		return true;
	}

	public static void Shutdown()
	{
		if (!_subscribed)
		{
			return;
		}
		AppDomain.CurrentDomain.UnhandledException -= OnFatalException;
		_subscribed = false;
	}

	private static void WriteError(string message)
	{
		try
		{
			Console.Error.WriteLine(message);
		}
		catch
		{
		}
	}

	private static void OnFatalException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is not Exception exception)
		{
			return;
		}
		string text = exception.ToString();
		bool gpuFailure = text.Contains("WebGPU", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("WgpuContext", StringComparison.Ordinal)
			|| text.Contains("No suitable adapter", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("failed to create surface", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("EGL_NOT_INITIALIZED", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("VK_ERROR", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("GLXBad", StringComparison.OrdinalIgnoreCase);
		if (!gpuFailure || Volatile.Read(ref _confirmed) != 0)
		{
			return;
		}
		if (_supervised)
		{
			WriteRendererMarker(_activeStage, false, 1);
			Environment.Exit(RendererFailedExitCode);
			return;
		}
		if (_activeStage == SoftwareStage)
		{
			WriteError("Voidstrap could not start a GPU or software renderer on this system. Install your graphics drivers, or the Mesa Vulkan drivers package for software rendering, and try again.");
			Environment.Exit(1);
			return;
		}
		if (Environment.GetEnvironmentVariable(ForceGpuFlag) == "1")
		{
			WriteError("Voidstrap could not start the requested GPU renderer. Update your graphics drivers or remove VOIDSTRAP_USE_GPU.");
			Environment.Exit(1);
			return;
		}
		string nextStage = NextStage(_activeStage);
		WriteError(nextStage == HardwareGlStage
			? "Voidstrap could not start the Vulkan renderer, retrying with OpenGL on your graphics card."
			: "Voidstrap could not start an accelerated renderer, retrying with software rendering. Expect reduced performance until your graphics drivers are updated.");
		try
		{
			WriteRendererMarker(nextStage, false, 0);
			string? executable = LinuxAppImageHost.ResolveApplicationPath(Environment.ProcessPath ?? string.Empty);
			if (string.IsNullOrEmpty(executable))
			{
				return;
			}
			ProcessStartInfo startInfo = CreateStartInfo(executable, CurrentArguments(), nextStage, string.Empty);
			startInfo.Environment.Remove(ConfiguredFlag);
			if (Process.Start(startInfo) != null)
			{
				Environment.Exit(0);
			}
		}
		catch
		{
		}
	}

	private static void ReplaceProcess(ProcessStartInfo startInfo)
	{
		List<nint> allocated = [];
		nint Utf8(string value)
		{
			nint pointer = Marshal.StringToCoTaskMemUTF8(value);
			allocated.Add(pointer);
			return pointer;
		}
		try
		{
			nint[] argv = [Utf8(startInfo.FileName), .. startInfo.ArgumentList.Select(Utf8), 0];
			nint[] envp = [.. startInfo.Environment.Where(entry => entry.Value != null).Select(entry => Utf8(entry.Key + "=" + entry.Value)), 0];
			Execve(argv[0], argv, envp);
		}
		catch (Exception)
		{
		}
		finally
		{
			foreach (nint pointer in allocated)
			{
				Marshal.FreeCoTaskMem(pointer);
			}
		}
	}

	[LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
	private static partial int Execve(nint path, nint[] argv, nint[] envp);

	[LibraryImport("libc", EntryPoint = "getppid")]
	private static partial int GetParentProcessId();
}
