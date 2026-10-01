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

	private const string SafeModeFlag = "VOIDSTRAP_SAFE_MODE";

	private const string PlainSurfacesFlag = "VOIDSTRAP_PLAIN_SURFACES";

	private const int SafeLaunchCount = 5;

	private static int _safeLaunches;

	private static bool _safeMode;

	private const string DefaultStage = "default";

	private const string OpaqueStage = "opaque";

	private const string HardwareGlStage = "gl";

	private const string OpaqueWindowsFlag = "VOIDSTRAP_OPAQUE_WINDOWS";

	private const string NvidiaDriverVersionPath = "/proc/driver/nvidia/version";

	private const string SoftwareStage = "software";

	private const string ApplicationName = "Voidstrap";

	private const string MissingVulkanDriver = "/nonexistent/voidstrap-no-vulkan.json";

	private const string VulkanVendorFileName = "vulkan-vendor";

	private static readonly string[] UserVulkanDriverKeys = ["VK_DRIVER_FILES", "VK_ICD_FILENAMES", "VK_ADD_DRIVER_FILES", "VK_LOADER_DRIVERS_SELECT", "VK_LOADER_DRIVERS_DISABLE"];

	private static bool _driverFilterAllowed;

	private const int RendererFailedExitCode = 86;

	private static readonly int SoftwareThreadCount = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 8);

	private static bool _subscribed;

	private static bool _supervised;

	private static string _activeStage = DefaultStage;

	private static int _probeStarted;

	private static bool _backgroundHelper;

	private static readonly string[] BackgroundHelperFlags = ["deferredcleanup", "assetwarpguard", "assetwarpcleanup", "nvapply", "telemetryblock", "orcredirect"];

	private static int _confirmed;

	public static string ActiveStage => _activeStage;

	public static bool SafeMode => _safeMode;

	internal static bool UsesOpenGl => _activeStage == HardwareGlStage
		|| (_activeStage == SoftwareStage && LavapipeDrivers().Length == 0);

	public static bool OpaqueWindows => _activeStage is OpaqueStage or SoftwareStage
		|| Environment.GetEnvironmentVariable(OpaqueWindowsFlag) == "1";

	[ModuleInitializer]
	internal static void Initialize()
	{
		Voidstrap.UI.LinuxUiPerformance.Mark("Module initializer entered");
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
		if (IsBackgroundHelper(CurrentArguments()))
		{
			_backgroundHelper = true;
			return;
		}
		if (IsConfiguredForThisProcess())
		{
			_activeStage = NormaliseStage(Environment.GetEnvironmentVariable(GpuRetryFlag));
			ApplyStageLibraries();
			_supervised = Environment.GetEnvironmentVariable(SupervisedFlag) == "1";
			_safeMode = DecideSafeMode();
			RestoreChildEnvironment();
			return;
		}
		long fontStarted = Stopwatch.GetTimestamp();
		TextFontInstaller.Install();
		Voidstrap.UI.LinuxUiPerformance.Duration("Font setup", fontStarted);
		try
		{
			bool headless = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
				&& string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
			if (headless)
			{
				WriteError("Voidstrap needs a desktop session with X11 or Wayland, but no display was found. Start it from your desktop instead of a text console or SSH session.");
				if (CurrentArguments().Length == 0)
				{
					Environment.Exit(1);
				}
			}
			long rendererStarted = Stopwatch.GetTimestamp();
			string stage = ChooseStage(out bool confirmed);
			_driverFilterAllowed = confirmed;
			if (!confirmed)
				DeleteVulkanVendor();
			Voidstrap.UI.LinuxUiPerformance.Duration("Renderer selection", rendererStarted);
			_activeStage = stage;
			_safeMode = DecideSafeMode();
			string? executable = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(executable) && !headless)
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
			foreach (KeyValuePair<string, string?> entry in CreateStartInfo(executable ?? ApplicationName, [], stage, "pid:" + Environment.ProcessId).Environment)
			{
				if (entry.Key.Length > 0 && !entry.Key.Contains('='))
				{
					Environment.SetEnvironmentVariable(entry.Key, entry.Value);
				}
			}
			ApplyStageLibraries();
		}
		catch (Exception)
		{
		}
	}

	internal static readonly string[] BackendNames = ["Auto", "Vulkan", "OpenGL", "Software"];

	private static bool IsBackgroundHelper(string[] arguments)
	{
		foreach (string argument in arguments)
		{
			if (argument.Length > 1 && argument[0] == '-' && Array.IndexOf(BackgroundHelperFlags, argument[1..].ToLowerInvariant()) >= 0)
			{
				return true;
			}
		}
		return false;
	}

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
			if (stripped.Length == 0)
				_ = UnsetNativeEnvironment("LD_LIBRARY_PATH");
			else
				_ = SetNativeEnvironment("LD_LIBRARY_PATH", stripped, 1);
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
		if (string.IsNullOrEmpty(requested))
			requested = StageForWgpuBackend(Environment.GetEnvironmentVariable("WGPU_BACKEND"));
		string stage = !string.IsNullOrEmpty(requested)
			? NormaliseStage(requested)
			: Environment.GetEnvironmentVariable(ForceGpuFlag) == "1" || recorded.Length == 0 ? DefaultStage : recorded;
		if (string.IsNullOrEmpty(requested) && !CanRunStage(stage))
			stage = DefaultStage;
		if (string.IsNullOrEmpty(requested) && stage is SoftwareStage or HardwareGlStage && recorded == stage && File.Exists(ProbeFallbackPath) && ProbeVulkan() == VulkanSupport.Hardware)
		{
			DeleteProbeFallbackNote();
			WriteError("Vulkan now works on your graphics card, so Voidstrap switches back to hardware rendering.");
			stage = DefaultStage;
		}
		else if (string.IsNullOrEmpty(requested) && !(markerConfirmed && recorded == stage) && stage is DefaultStage or OpaqueStage)
			stage = AdjustForVulkan(stage);
		confirmed = markerConfirmed && recorded == stage;
		return stage;
	}

	private enum VulkanSupport
	{
		Hardware,
		SoftwareOnly,
		None
	}

	private static string AdjustForVulkan(string stage)
	{
		VulkanSupport support = ProbeVulkan();
		if (support == VulkanSupport.Hardware)
			return stage;
		if (support == VulkanSupport.SoftwareOnly && SoftwareRendererAvailable())
		{
			WriteError("No graphics card is available through Vulkan, so Voidstrap starts with software rendering. Install or update your graphics drivers for full speed.");
			WriteProbeFallbackNote();
			return SoftwareStage;
		}
		if (support == VulkanSupport.None && CanUseHardwareGl())
		{
			WriteError("Vulkan is not working on this system, so Voidstrap starts with OpenGL on your graphics card. Install or update your Vulkan drivers for the best results.");
			WriteProbeFallbackNote();
			return HardwareGlStage;
		}
		if (support == VulkanSupport.None && LavapipeDrivers().Length > 0)
		{
			WriteError("Vulkan is not working with your graphics drivers, so Voidstrap starts with software rendering. Install or update your graphics drivers for full speed.");
			WriteProbeFallbackNote();
			return SoftwareStage;
		}
		return stage;
	}

	private static string ProbeFallbackPath => Path.Combine(Path.GetDirectoryName(RendererMarkerPath) ?? string.Empty, "renderer-probe-fallback");

	private static void WriteProbeFallbackNote()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(ProbeFallbackPath)!);
			File.WriteAllText(ProbeFallbackPath, "vulkan");
		}
		catch (Exception)
		{
		}
	}

	private static void DeleteProbeFallbackNote()
	{
		try
		{
			File.Delete(ProbeFallbackPath);
		}
		catch (Exception)
		{
		}
	}

	private static unsafe VulkanSupport ProbeVulkan()
	{
		nint instance = 0;
		try
		{
			byte* createInfo = stackalloc byte[64];
			new Span<byte>(createInfo, 64).Clear();
			*(int*)createInfo = 1;
			if (VkCreateInstance(createInfo, 0, &instance) != 0 || instance == 0)
				return VulkanSupport.None;

			uint count = 0;
			if (VkEnumeratePhysicalDevices(instance, &count, null) != 0 || count == 0)
				return VulkanSupport.None;

			uint capacity = Math.Min(count, 16u);
			nint* devices = stackalloc nint[(int)capacity];
			int listed = VkEnumeratePhysicalDevices(instance, &capacity, devices);
			if (listed != 0 && listed != 5)
				return VulkanSupport.None;

			byte* properties = stackalloc byte[4096];
			bool hardware = false;
			for (int index = 0; index < capacity; index++)
			{
				new Span<byte>(properties, 4096).Clear();
				VkGetPhysicalDeviceProperties(devices[index], properties);
				int deviceType = *(int*)(properties + 16);
				if (deviceType is 1 or 2 or 3)
					hardware = true;
			}
			if (!CanCreateWindowInstance())
				return VulkanSupport.None;
			return hardware ? VulkanSupport.Hardware : VulkanSupport.SoftwareOnly;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return VulkanSupport.None;
		}
		finally
		{
			if (instance != 0)
				VkDestroyInstance(instance, 0);
		}
	}

	private static unsafe bool CanCreateWindowInstance()
	{
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
			return true;
		nint surface = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("VK_KHR_surface");
		nint xlib = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("VK_KHR_xlib_surface");
		nint instance = 0;
		try
		{
			nint* names = stackalloc nint[2];
			names[0] = surface;
			names[1] = xlib;
			byte* createInfo = stackalloc byte[64];
			new Span<byte>(createInfo, 64).Clear();
			*(int*)createInfo = 1;
			*(uint*)(createInfo + 48) = 2;
			*(nint**)(createInfo + 56) = names;
			return VkCreateInstance(createInfo, 0, &instance) == 0 && instance != 0;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
		finally
		{
			if (instance != 0)
				VkDestroyInstance(instance, 0);
			System.Runtime.InteropServices.Marshal.FreeHGlobal(surface);
			System.Runtime.InteropServices.Marshal.FreeHGlobal(xlib);
		}
	}

	private static bool CanRunStage(string stage)
	{
		return stage switch
		{
			HardwareGlStage => CanUseHardwareGl(),
			SoftwareStage => SoftwareRendererAvailable(),
			_ => true
		};
	}

	private static string? StageForWgpuBackend(string? backend)
	{
		return backend?.Trim().ToLowerInvariant() switch
		{
			"vulkan" or "vk" => DefaultStage,
			"gl" or "gles" or "opengl" => HardwareGlStage,
			_ => null
		};
	}

	private static bool DecideSafeMode()
	{
		string? forced = Environment.GetEnvironmentVariable(SafeModeFlag);
		if (forced == "0")
		{
			return false;
		}
		ReadRendererMarker(out string recorded, out bool confirmed, out int attempts, out int safeLaunches);
		_safeLaunches = recorded.Length > 0 && !confirmed && attempts > 0 ? SafeLaunchCount : safeLaunches;
		return forced == "1" || _safeLaunches > 0;
	}

	private static string[] CurrentArguments()
	{
		return Environment.GetCommandLineArgs().Skip(1).ToArray();
	}

	private static void Supervise(string executable, string stage)
	{
		for (string current = stage; ; current = NextStage(current))
		{
			if (current == SoftwareStage && !SoftwareRendererAvailable())
			{
				ExitWithoutSoftwareRenderer();
			}
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
					WriteRendererMarker(DefaultStage, false, 0);
					WriteError("Voidstrap could not start a GPU or software renderer on this system. Install your graphics drivers, or the Mesa Vulkan drivers package for software rendering, and try again. The next launch tries every renderer again.");
				}
				Environment.Exit(exitCode);
			}
			string next = NextStage(current);
			if (next == SoftwareStage && !SoftwareRendererAvailable())
			{
				ExitWithoutSoftwareRenderer();
			}
			WriteError(RetryMessage(next));
		}
	}

	private static bool SoftwareRendererAvailable()
	{
		return LavapipeDrivers().Length > 0 || HasOpenGlBackend();
	}

	private static void ExitWithoutSoftwareRenderer()
	{
		WriteRendererMarker(DefaultStage, false, 0);
		WriteError("Voidstrap could not start the renderer on your graphics card, and no software renderer is installed. Install the Mesa Vulkan drivers package (lavapipe) or update your graphics drivers, then start Voidstrap again.");
		Environment.Exit(RendererFailedExitCode);
	}

	private static string RetryMessage(string nextStage)
	{
		return nextStage switch
		{
			OpaqueStage => "Voidstrap could not start the Vulkan renderer with transparent windows, retrying with opaque windows.",
			HardwareGlStage => "Voidstrap could not start the Vulkan renderer, retrying with OpenGL on your graphics card.",
			_ => "Voidstrap could not start an accelerated renderer, retrying with software rendering. Expect reduced performance until your graphics drivers are updated."
		};
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
		void SetIfMissing(string key, string value)
		{
			if (!environment.ContainsKey(key))
			{
				Set(key, value);
			}
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
				string? mesaVendor = MesaEglVendorFile();
				if (mesaVendor != null)
				{
					SetIfMissing("__EGL_VENDOR_LIBRARY_FILENAMES", mesaVendor);
				}
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
			else if (_driverFilterAllowed && !UserVulkanDriverKeys.Any(environment.ContainsKey) && VulkanDriverSelection() is string selection)
			{
				Set("VK_LOADER_DRIVERS_SELECT", selection);
			}
		}
		if (backend != "Software" && Environment.GetEnvironmentVariable(PlainSurfacesFlag) != "0")
		{
			SetIfMissing("MESA_VK_WSI_DEBUG", "linear");
			SetIfMissing("AMD_DEBUG", "nodcc");
			SetIfMissing("RADV_DEBUG", "nodcc");
			SetIfMissing("INTEL_DEBUG", "noccs");
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
			foreach (string name in new[] { "libEGL.so.1", "libEGL.so" })
			{
				string link = Path.Combine(directory, name);
				if (!string.Equals(new FileInfo(link).LinkTarget, library, StringComparison.Ordinal))
				{
					File.Delete(link);
					File.CreateSymbolicLink(link, library);
				}
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

	private static string? MesaEglVendorFile()
	{
		foreach (string directory in new[] { "/usr/share/glvnd/egl_vendor.d", "/etc/glvnd/egl_vendor.d" })
		{
			try
			{
				if (!Directory.Exists(directory))
					continue;
				string? mesa = Directory.EnumerateFiles(directory, "*mesa*.json").OrderBy(static path => path, StringComparer.Ordinal).FirstOrDefault();
				if (mesa != null)
					return mesa;
			}
			catch (Exception)
			{
			}
		}
		return null;
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
			DefaultStage => OpaqueStage,
			OpaqueStage => CanUseHardwareGl() ? HardwareGlStage : SoftwareStage,
			HardwareGlStage => SoftwareStage,
			_ => SoftwareStage
		};
	}

	private static bool CanUseHardwareGl()
	{
		return HasOpenGlBackend() && !File.Exists(NvidiaDriverVersionPath);
	}

	private const string OpenGlWgpuLibraryName = "libwgpu_native_gl.so";

	private static string OpenGlWgpuLibrary => Path.Combine(AppContext.BaseDirectory, OpenGlWgpuLibraryName);

	private static void ApplyStageLibraries()
	{
		if (!UsesOpenGl || !HasOpenGlBackend())
			return;
		string library = File.Exists(OpenGlWgpuLibrary) ? OpenGlWgpuLibrary : Path.Combine(AppContext.BaseDirectory, "libwgpu_native.so");
		if (!File.Exists(library))
			return;
		try
		{
			Type? resolverType = Type.GetType("Silk.NET.Core.Loader.PathResolver, Silk.NET.Core", false);
			object? resolver = resolverType?.GetProperty("Default", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
			if (resolver?.GetType().GetProperty("Resolvers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(resolver) is not List<Func<string, IEnumerable<string>>> resolvers)
				return;
			resolvers.Insert(0, name => name.Contains("wgpu_native", StringComparison.Ordinal) ? [library] : []);
		}
		catch (Exception)
		{
		}
		PrepareOpenGlShaders();
	}

	private static readonly string[] OpenGlShaderAssemblies = ["ProGPU.Backend", "ProGPU.Compute", "ProGPU.Vector", "ProGPU.Scene", "ProGPU.Text", "ProGPU.Wpf"];

	private static readonly System.Text.RegularExpressions.Regex OpenGlReservedShaderNames = new(@"\b(packed)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	private static void PrepareOpenGlShaders()
	{
		try
		{
			const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
			Type? resourceType = Type.GetType("ProGPU.Backend.ShaderResource, ProGPU.Backend", false);
			Type? keyType = resourceType?.GetNestedType("ResourceKey", System.Reflection.BindingFlags.NonPublic);
			if (keyType is null || resourceType?.GetField("s_sources", hidden)?.GetValue(null) is not System.Collections.IDictionary sources)
				return;
			foreach (string assemblyName in OpenGlShaderAssemblies)
			{
				System.Reflection.Assembly assembly;
				try
				{
					assembly = System.Reflection.Assembly.Load(assemblyName);
				}
				catch (Exception)
				{
					continue;
				}
				string prefix = assemblyName + ".Shaders.";
				foreach (string resource in assembly.GetManifestResourceNames())
				{
					if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".wgsl", StringComparison.OrdinalIgnoreCase))
						continue;
					using Stream? stream = assembly.GetManifestResourceStream(resource);
					if (stream is null)
						continue;
					using StreamReader reader = new(stream, System.Text.Encoding.UTF8, true);
					string source = reader.ReadToEnd();
					string renamed = OpenGlReservedShaderNames.Replace(source, "voidstrap_$1");
					if (ReferenceEquals(renamed, source) || renamed == source)
						continue;
					object? key = Activator.CreateInstance(keyType, assembly, resource[prefix.Length..]);
					if (key is not null && !sources.Contains(key))
						sources[key] = renamed;
				}
			}
		}
		catch (Exception)
		{
		}
	}

	private static bool HasOpenGlBackend()
	{
		try
		{
			if (File.Exists(OpenGlWgpuLibrary))
				return true;
			string library = Path.Combine(AppContext.BaseDirectory, "libwgpu_native.so");
			if (!File.Exists(library))
				return true;
			byte[] marker = "libEGL.so"u8.ToArray();
			return File.ReadAllBytes(library).AsSpan().IndexOf(marker) >= 0;
		}
		catch (Exception)
		{
			return true;
		}
	}

	private static string NormaliseStage(string? stage)
	{
		return stage switch
		{
			OpaqueStage => OpaqueStage,
			HardwareGlStage => HardwareGlStage,
			SoftwareStage => SoftwareStage,
			_ => DefaultStage
		};
	}

	private static void ReadRendererMarker(out string stage, out bool confirmed, out int attempts)
	{
		ReadRendererMarker(out stage, out confirmed, out attempts, out _);
	}

	private static void ReadRendererMarker(out string stage, out bool confirmed, out int attempts, out int safeLaunches)
	{
		stage = string.Empty;
		confirmed = false;
		attempts = 0;
		safeLaunches = 0;
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

			if (parts.Length > 3 && int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int safe))
			{
				safeLaunches = Math.Clamp(safe, 0, SafeLaunchCount);
			}
		}
		catch (Exception)
		{
			stage = string.Empty;
			confirmed = false;
			attempts = 0;
			safeLaunches = 0;
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

			int safeLaunches = confirmed ? Math.Max(_safeLaunches - 1, 0) : _safeLaunches;
			File.WriteAllText(path, stage + "|" + (confirmed ? "ok" : "pending") + "|" + attempts.ToString(CultureInfo.InvariantCulture) + "|" + safeLaunches.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception)
		{
		}
	}

	public static void BeginRendererProbe()
	{
		if (!OperatingSystem.IsLinux() || _backgroundHelper)
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

	public static bool IsRendererActive()
	{
#if CROSSPLAT
		return ProGPU.Backend.WgpuContext.TryGetFirstActiveContext(out _);
#else
		return true;
#endif
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
			RecordVulkanVendor();
		}
		return true;
	}

	private static string VulkanVendorPath => Path.Combine(Path.GetDirectoryName(RendererMarkerPath) ?? string.Empty, VulkanVendorFileName);

	private static void RecordVulkanVendor()
	{
#if CROSSPLAT
		try
		{
			string? vendor = null;
			if (ProGPU.Backend.WgpuContext.TryGetFirstActiveContext(out ProGPU.Backend.WgpuContext? context)
				&& string.Equals(context.AdapterBackendType.ToString(), "Vulkan", StringComparison.OrdinalIgnoreCase))
			{
				vendor = VulkanVendorForAdapter(context.AdapterName ?? string.Empty);
			}

			if (vendor == null)
			{
				DeleteVulkanVendor();
				return;
			}

			string path = VulkanVendorPath;
			if (File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), vendor, StringComparison.Ordinal))
				return;
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, vendor);
		}
		catch (Exception)
		{
		}
#endif
	}

	private static string? VulkanVendorForAdapter(string adapter)
	{
		if (adapter.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || adapter.Contains("lavapipe", StringComparison.OrdinalIgnoreCase) || adapter.Contains("swiftshader", StringComparison.OrdinalIgnoreCase))
			return null;
		if (adapter.Contains("(NVK", StringComparison.OrdinalIgnoreCase))
			return "nouveau";
		if (adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
			return "nvidia";
		if (adapter.Contains("(RADV", StringComparison.OrdinalIgnoreCase))
			return "radeon";
		if (adapter.Contains("AMD", StringComparison.OrdinalIgnoreCase) || adapter.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
			return "amd";
		if (adapter.Contains("Intel", StringComparison.OrdinalIgnoreCase))
			return "intel";
		return null;
	}

	private static string? VulkanDriverSelection()
	{
		try
		{
			string path = VulkanVendorPath;
			if (!File.Exists(path))
				return null;

			(string pattern, string pciVendor)? choice = File.ReadAllText(path).Trim() switch
			{
				"nvidia" => ("*nvidia*", "0x10de"),
				"nouveau" => ("*nouveau*", "0x10de"),
				"radeon" => ("*radeon*", "0x1002"),
				"amd" => ("*amd*,*radeon*", "0x1002"),
				"intel" => ("*intel*", "0x8086"),
				_ => null
			};
			if (choice is not { } selected || !HasGpuFromVendor(selected.pciVendor))
			{
				DeleteVulkanVendor();
				return null;
			}

			return selected.pattern;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static bool HasGpuFromVendor(string pciVendor)
	{
		try
		{
			foreach (string card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
			{
				string vendorFile = Path.Combine(card, "device", "vendor");
				if (File.Exists(vendorFile) && string.Equals(File.ReadAllText(vendorFile).Trim(), pciVendor, StringComparison.OrdinalIgnoreCase))
					return true;
			}
		}
		catch (Exception)
		{
		}
		return false;
	}

	private static void DeleteVulkanVendor()
	{
		try
		{
			File.Delete(VulkanVendorPath);
		}
		catch (Exception)
		{
		}
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
			WriteRendererMarker(DefaultStage, false, 0);
			WriteError("Voidstrap could not start a GPU or software renderer on this system. Install your graphics drivers, or the Mesa Vulkan drivers package for software rendering, and try again. The next launch tries every renderer again.");
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
		WriteError(RetryMessage(nextStage));
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

	[LibraryImport("libvulkan.so.1", EntryPoint = "vkCreateInstance")]
	private static unsafe partial int VkCreateInstance(byte* createInfo, nint allocator, nint* instance);

	[LibraryImport("libvulkan.so.1", EntryPoint = "vkEnumeratePhysicalDevices")]
	private static unsafe partial int VkEnumeratePhysicalDevices(nint instance, uint* count, nint* devices);

	[LibraryImport("libvulkan.so.1", EntryPoint = "vkGetPhysicalDeviceProperties")]
	private static unsafe partial void VkGetPhysicalDeviceProperties(nint device, byte* properties);

	[LibraryImport("libvulkan.so.1", EntryPoint = "vkDestroyInstance")]
	private static partial void VkDestroyInstance(nint instance, nint allocator);

	[LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
	private static partial int Execve(nint path, nint[] argv, nint[] envp);

	[LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int SetNativeEnvironment(string name, string value, int overwrite);

	[LibraryImport("libc", EntryPoint = "unsetenv", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int UnsetNativeEnvironment(string name);

	[LibraryImport("libc", EntryPoint = "getppid")]
	private static partial int GetParentProcessId();
}
