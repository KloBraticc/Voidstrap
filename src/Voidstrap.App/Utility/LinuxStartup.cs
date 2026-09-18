using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class LinuxStartup
{
	private const string ConfiguredFlag = "VOIDSTRAP_GL_CONFIGURED";

	private const string ForceGpuFlag = "VOIDSTRAP_USE_GPU";

	private const string GpuRetryFlag = "VOIDSTRAP_GPU_RETRY";

	private const string DefaultStage = "default";

	private const string HardwareGlStage = "gl";

	private const string SoftwareStage = "software";

	private const string ApplicationName = "Voidstrap";

	private const int MaxAttemptsPerStage = 2;

	private static readonly int SoftwareThreadCount = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 8);

	private static bool _subscribed;

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
		if (Environment.GetEnvironmentVariable(ConfiguredFlag) == "1")
		{
			_activeStage = NormaliseStage(Environment.GetEnvironmentVariable(GpuRetryFlag));
			return;
		}
		TextFontInstaller.Install();
		Environment.SetEnvironmentVariable(ConfiguredFlag, "1");
		Environment.SetEnvironmentVariable("RESOURCE_NAME", ApplicationName);
		Environment.SetEnvironmentVariable("SDL_VIDEO_X11_WMCLASS", ApplicationName);
		RecoverFromPreviousRendererCrash();
	}

	internal static readonly string[] BackendNames = ["Auto", "Vulkan", "OpenGL", "Software"];

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

	internal static bool HasHardwareVulkan()
	{
		try
		{
			string[] directories =
			[
				"/usr/share/vulkan/icd.d",
				"/etc/vulkan/icd.d",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "vulkan", "icd.d")
			];

			foreach (string directory in directories)
			{
				if (!Directory.Exists(directory))
					continue;
				foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
				{
					string name = Path.GetFileName(file);
					if (name.Contains("lvp", StringComparison.OrdinalIgnoreCase)
						|| name.Contains("lavapipe", StringComparison.OrdinalIgnoreCase)
						|| name.Contains("swrast", StringComparison.OrdinalIgnoreCase))
						continue;
					return true;
				}
			}
		}
		catch (Exception)
		{
		}

		return false;
	}

	private static void ApplySelectedBackend()
	{
		string selected = ReadConfiguredBackend();
		string backend = selected switch
		{
			"Vulkan" => "vulkan",
			"OpenGL" => "gl",
			"Software" => "gl",
			_ => HasHardwareVulkan() ? "vulkan" : "gl"
		};

		Environment.SetEnvironmentVariable("WGPU_BACKEND", backend);
		Environment.SetEnvironmentVariable("VOIDSTRAP_RENDER_BACKEND", selected);
		if (selected == "Software")
		{
			Environment.SetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE", "1");
			Environment.SetEnvironmentVariable("LP_NUM_THREADS", SoftwareThreadCount.ToString(CultureInfo.InvariantCulture));
		}
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

	private static void RecoverFromPreviousRendererCrash()
	{
		string requested = NormaliseStage(Environment.GetEnvironmentVariable(GpuRetryFlag));
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GpuRetryFlag)))
		{
			_activeStage = requested;
			ApplyStageEnvironment(requested);
			return;
		}

		if (Environment.GetEnvironmentVariable(ForceGpuFlag) == "1")
		{
			_activeStage = DefaultStage;
			return;
		}

		ReadRendererMarker(out string recordedStage, out bool confirmed, out int attempts);

		string stage;
		if (confirmed)
		{
			stage = recordedStage;
		}
		else if (recordedStage.Length == 0)
		{
			stage = DefaultStage;
		}
		else if (attempts < MaxAttemptsPerStage)
		{
			stage = recordedStage;
		}
		else
		{
			stage = NextStage(recordedStage);
			if (stage != recordedStage)
			{
				Console.Error.WriteLine(stage == HardwareGlStage
					? "The last Voidstrap start could not initialise the default renderer, retrying with OpenGL on your graphics card."
					: "The last Voidstrap start could not initialise an accelerated renderer, falling back to software rendering.");
			}
		}

		_activeStage = stage;
		ApplyStageEnvironment(stage);
		if (stage == DefaultStage)
		{
			ApplySelectedBackend();
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

	private static void ApplyStageEnvironment(string stage)
	{
		if (stage == DefaultStage)
		{
			return;
		}

		Environment.SetEnvironmentVariable(GpuRetryFlag, stage);
		Environment.SetEnvironmentVariable("WGPU_BACKEND", "gl");
		Environment.SetEnvironmentVariable("WGPU_POWER_PREF", stage == HardwareGlStage ? "high" : "low");
		if (stage == SoftwareStage)
		{
			Environment.SetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE", "1");
			Environment.SetEnvironmentVariable("LP_NUM_THREADS", SoftwareThreadCount.ToString(CultureInfo.InvariantCulture));
		}
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

	public static void MarkRendererHealthy()
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}
		if (Volatile.Read(ref _probeStarted) == 0)
		{
			return;
		}
		if (Interlocked.Exchange(ref _confirmed, 1) != 0)
		{
			return;
		}

		WriteRendererMarker(_activeStage, true, 0);
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
		if (!gpuFailure)
		{
			return;
		}
		if (_activeStage == SoftwareStage)
		{
			try
			{
				Console.Error.WriteLine("Voidstrap could not start a GPU or software renderer on this system. Update your graphics drivers and try again.");
			}
			catch
			{
			}
			Environment.Exit(1);
			return;
		}
		if (Environment.GetEnvironmentVariable(ForceGpuFlag) == "1")
		{
			try
			{
				Console.Error.WriteLine("Voidstrap could not start the requested GPU renderer. Update your graphics drivers or remove VOIDSTRAP_USE_GPU.");
			}
			catch
			{
			}
			Environment.Exit(1);
			return;
		}
		string nextStage = NextStage(_activeStage);
		try
		{
			Console.Error.WriteLine(nextStage == HardwareGlStage
				? "Voidstrap could not start the default renderer, retrying with OpenGL on your graphics card."
				: "Voidstrap could not start an accelerated renderer, retrying with software rendering. Expect reduced performance until your graphics drivers are updated.");
		}
		catch
		{
		}
		try
		{
			WriteRendererMarker(nextStage, false, 0);
			string? executable = LinuxAppImageHost.ResolveApplicationPath(Environment.ProcessPath ?? string.Empty);
			if (string.IsNullOrEmpty(executable))
			{
				return;
			}
			ProcessStartInfo startInfo = new ProcessStartInfo(executable)
			{
				UseShellExecute = false
			};
			string[] arguments = Environment.GetCommandLineArgs();
			for (int i = 1; i < arguments.Length; i++)
			{
				startInfo.ArgumentList.Add(arguments[i]);
			}
			startInfo.Environment[GpuRetryFlag] = nextStage;
			startInfo.Environment[ConfiguredFlag] = "1";
			startInfo.Environment["RESOURCE_NAME"] = ApplicationName;
			startInfo.Environment["SDL_VIDEO_X11_WMCLASS"] = ApplicationName;
			startInfo.Environment["WGPU_BACKEND"] = "gl";
			startInfo.Environment["WGPU_POWER_PREF"] = nextStage == HardwareGlStage ? "high" : "low";
			if (nextStage == SoftwareStage)
			{
				startInfo.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
				startInfo.Environment["LP_NUM_THREADS"] = SoftwareThreadCount.ToString(CultureInfo.InvariantCulture);
			}
			if (Process.Start(startInfo) != null)
			{
				Environment.Exit(0);
			}
		}
		catch
		{
		}
	}
}
