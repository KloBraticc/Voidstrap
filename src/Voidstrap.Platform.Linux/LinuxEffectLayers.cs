using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core;
using Voidstrap.Platform;

namespace Voidstrap.Platform.Linux;

public sealed record LinuxEffectOptions(
	bool Enabled = false,
	string? AntiAliasing = null,
	bool AntiAliasingUltra = false,
	bool Sharpening = false,
	float SharpnessAmount = 0.4f,
	string? GradingShader = null,
	string? HomepageShader = null,
	string? HomepageMediaPath = null,
	int FrameGenMultiplier = 0);

public sealed record LinuxEffectLayerState(bool ShaderLayerInstalled, bool FrameGenLayerInstalled);

public static class LinuxEffectLayers
{
	public const string ShaderLayerId = "org.freedesktop.Platform.VulkanLayer.vkBasalt";

	public const string FrameGenLayerId = "org.freedesktop.Platform.VulkanLayer.lsfgvk";

	public const string MangoHudLayerId = "org.freedesktop.Platform.VulkanLayer.MangoHud";

	public const string DefaultLayerBranch = "25.08";

	private const string VulkanLayerExtension = "org.freedesktop.Platform.VulkanLayer";

	private const string RemoteName = "flathub";

	private const string RemoteUrl = "https://dl.flathub.org/repo/flathub.flatpakrepo";

	private static readonly object BranchGate = new();

	private static string? _layerBranch;

	public static string LayerBranch
	{
		get
		{
			lock (BranchGate)
			{
				if (_layerBranch is not null)
					return _layerBranch;

				string? resolved = ResolveLayerBranch();
				if (resolved is null)
					return DefaultLayerBranch;

				_layerBranch = resolved;
				return resolved;
			}
		}
	}

	private static string FlatpakArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";

	public static string ConfigDirectory
	{
		get
		{
			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(home, ".local", "state");
			return Path.Combine(state, "voidstrap", "runtime", "effects");
		}
	}

	public static string ConfigFile => Path.Combine(ConfigDirectory, "vkBasalt.conf");

	public static string GradingShaderFile => Path.Combine(ConfigDirectory, "VoidstrapGrade.fx");

	public static string HomepageShaderFile => Path.Combine(ConfigDirectory, "VoidstrapHomepage.fx");

	public static string HomepageMediaFile(string sourcePath)
	{
		string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
		return Path.Combine(ConfigDirectory, "VoidstrapHomepageMedia" + extension);
	}

	public static bool IsInstalled(string layerId)
	{
		return IsNativeInstalled(layerId);
	}

	private static bool IsNativeInstalled(string layerId)
	{
		if (string.IsNullOrWhiteSpace(layerId))
			return false;

		try
		{
			string branch = LayerBranch;
			if (LinuxFlatpakHost.IsSandboxed)
			{
				SystemProcessService processes = new();
				if (!LinuxFlatpakHost.TryCreateCommand(processes, ["info", layerId + "//" + branch], out ProcessCommand command))
					return false;
				OperationResult<ProcessExecution> result = processes.ExecuteAsync(command).GetAwaiter().GetResult();
				return result.Succeeded && result.Value is { ExitCode: 0 };
			}

			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string userInstallation = Environment.GetEnvironmentVariable("FLATPAK_USER_DIR") is { Length: > 0 } custom
				? custom
				: Path.Combine(home, ".local", "share", "flatpak");
			string[] roots =
			[
				Path.Combine(userInstallation, "runtime", layerId, FlatpakArchitecture, branch),
				Path.Combine("/var", "lib", "flatpak", "runtime", layerId, FlatpakArchitecture, branch)
			];

			foreach (string root in roots)
			{
				if (Directory.Exists(Path.Combine(root, "active", "files")))
					return true;
			}

			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static void RefreshState()
	{
		lock (BranchGate)
			_layerBranch = null;
		LinuxEffectLayerFallback.RemoveLegacyCopies();
	}

	public static LinuxEffectLayerState GetState()
	{
		return new LinuxEffectLayerState(IsInstalled(ShaderLayerId), IsInstalled(FrameGenLayerId));
	}

	public static async Task<OperationResult> InstallAsync(IProcessService processes, string layerId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		cancellationToken.ThrowIfCancellationRequested();
		if (IsInstalled(layerId))
		{
			cancellationToken.ThrowIfCancellationRequested();
			return OperationResult.Success();
		}

		if (LinuxFlatpakHost.TryCreateCommand(processes, ["remote-add", "--if-not-exists", "--user", RemoteName, RemoteUrl], out ProcessCommand remote))
			await processes.ExecuteAsync(remote, cancellationToken).ConfigureAwait(false);

		if (!LinuxFlatpakHost.TryCreateCommand(processes, ["install", "--user", "--noninteractive", RemoteName, layerId + "//" + LayerBranch], out ProcessCommand command))
			return OperationResult.Fail("FlatpakMissing", "Flatpak is not installed");

		OperationResult<ProcessExecution> result = await processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();

		if (!result.Succeeded || result.Value is null)
			return OperationResult.Fail("EffectLayerInstallFailed", result.Failure?.Message ?? "The effect layer could not be installed");

		if (result.Value.ExitCode != 0 && !IsInstalled(layerId))
		{
			OperationResult fallback = await LinuxEffectLayerFallback.PrepareAsync(processes, layerId, cancellationToken).ConfigureAwait(false);
			return fallback.Succeeded
				? fallback
				: OperationResult.Fail("EffectLayerInstallFailed", "The " + LayerBranch + " effect layer could not be installed: " + LastLine(result.Value.StandardError) + "\n" + fallback.Failure?.Message);
		}

		return IsInstalled(layerId)
			? OperationResult.Success()
			: OperationResult.Fail("EffectLayerInstallFailed", "The effect layer did not appear after installing");
	}

	private static string? ResolveLayerBranch()
	{
		try
		{
			SystemProcessService processes = new();
			OperationResult<LinuxSoberRuntimeProvider.SoberFlatpakSelection> sober = LinuxSoberRuntimeProvider.FindSoberInstallationAsync(processes, CancellationToken.None).GetAwaiter().GetResult();
			if (!sober.Succeeded || sober.Value is null)
				return null;
			string? runtime = ReadMetadataValue(processes, sober.Value.Reference, "Application", "runtime", sober.Value.Scope);
			if (string.IsNullOrWhiteSpace(runtime))
				return null;

			string? version = ReadMetadataValue(processes, sober.Value.Reference, "Extension " + VulkanLayerExtension, "version", sober.Value.Scope)
				?? ReadMetadataValue(processes, runtime, "Extension " + VulkanLayerExtension, "version", sober.Value.Scope)
				?? ReadMetadataValue(processes, runtime, "Extension " + VulkanLayerExtension, "version");
			return string.IsNullOrWhiteSpace(version) ? null : version;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string? ReadMetadataValue(IProcessService processes, string reference, string section, string key, string? scope = null)
	{
		List<string> arguments = ["info", "--show-metadata"];
		if (scope is not null)
			arguments.Add(scope);
		arguments.Add(reference);
		if (!LinuxFlatpakHost.TryCreateCommand(processes, arguments, out ProcessCommand command))
			return null;

		OperationResult<ProcessExecution> result = processes.ExecuteAsync(command).GetAwaiter().GetResult();
		if (!result.Succeeded || result.Value is not { ExitCode: 0 } execution)
			return null;

		string? current = null;
		string? listed = null;
		foreach (string raw in execution.StandardOutput.Split('\n'))
		{
			string line = raw.Trim();
			if (line.StartsWith('[') && line.EndsWith(']'))
			{
				current = line[1..^1];
				continue;
			}

			if (!string.Equals(current, section, StringComparison.Ordinal))
				continue;

			int separator = line.IndexOf('=');
			if (separator <= 0)
				continue;

			string name = line[..separator].Trim();
			string value = line[(separator + 1)..].Trim();
			if (string.Equals(name, key, StringComparison.Ordinal) && value.Length > 0)
				return value;
			if (string.Equals(name, key + "s", StringComparison.Ordinal))
				listed ??= value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } entries ? entries[0] : null;
		}

		return listed;
	}

	private static string LastLine(string text)
	{
		string[] lines = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return lines.Length == 0 ? "no details" : lines[^1];
	}

	public static OperationResult WriteConfiguration(LinuxEffectOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		try
		{
			Directory.CreateDirectory(ConfigDirectory);
			List<string> effects = [];

			if (!string.IsNullOrWhiteSpace(options.HomepageShader))
			{
				File.WriteAllText(HomepageShaderFile, options.HomepageShader);
				if (!string.IsNullOrWhiteSpace(options.HomepageMediaPath) && File.Exists(options.HomepageMediaPath))
				{
					foreach (string previous in Directory.EnumerateFiles(ConfigDirectory, "VoidstrapHomepageMedia.*"))
					{
						if (!string.Equals(previous, HomepageMediaFile(options.HomepageMediaPath), StringComparison.Ordinal))
							File.Delete(previous);
					}
					File.Copy(options.HomepageMediaPath, HomepageMediaFile(options.HomepageMediaPath), true);
				}
				effects.Add("voidstrapHomepage");
			}

			if (!string.IsNullOrWhiteSpace(options.GradingShader))
			{
				File.WriteAllText(GradingShaderFile, options.GradingShader);
				effects.Add("voidstrapGrade");
			}

			if (!string.IsNullOrWhiteSpace(options.AntiAliasing))
				effects.Add(options.AntiAliasing);

			if (options.Sharpening)
				effects.Add("cas");

			StringBuilder builder = new();
			builder.AppendLine("effects = " + (effects.Count == 0 ? "" : string.Join(":", effects)));
			builder.AppendLine("enableOnLaunch = True");
			builder.AppendLine("depthCapture = off");
			builder.AppendLine("toggleKey = Scroll_Lock");
			builder.AppendLine("reshadeIncludePath = " + ConfigDirectory);
			builder.AppendLine("reshadeTexturePath = " + ConfigDirectory);
			builder.AppendLine("casSharpness = " + options.SharpnessAmount.ToString("0.00", CultureInfo.InvariantCulture));
			if (string.Equals(options.AntiAliasing, "smaa", StringComparison.Ordinal))
			{
				builder.AppendLine("smaaEdgeDetection = luma");
				builder.AppendLine("smaaThreshold = " + (options.AntiAliasingUltra ? "0.05" : "0.1"));
				builder.AppendLine("smaaMaxSearchSteps = " + (options.AntiAliasingUltra ? "32" : "16"));
				builder.AppendLine("smaaMaxSearchStepsDiag = " + (options.AntiAliasingUltra ? "16" : "8"));
				builder.AppendLine("smaaCornerRounding = 25");
			}
			else if (string.Equals(options.AntiAliasing, "fxaa", StringComparison.Ordinal))
			{
				builder.AppendLine("fxaaQualitySubpix = " + (options.AntiAliasingUltra ? "1.00" : "0.75"));
				builder.AppendLine("fxaaQualityEdgeThreshold = " + (options.AntiAliasingUltra ? "0.063" : "0.125"));
				builder.AppendLine("fxaaQualityEdgeThresholdMin = 0.0312");
			}
			if (!string.IsNullOrWhiteSpace(options.GradingShader))
				builder.AppendLine("voidstrapGrade = " + GradingShaderFile);
			if (!string.IsNullOrWhiteSpace(options.HomepageShader))
				builder.AppendLine("voidstrapHomepage = " + HomepageShaderFile);

			File.WriteAllText(ConfigFile, builder.ToString());
			return OperationResult.Success();
		}
		catch (Exception ex)
		{
			return OperationResult.Fail("EffectConfigFailed", "The effect configuration could not be written: " + ex.Message);
		}
	}

	public static IReadOnlyList<string> BuildLaunchArguments(LinuxEffectOptions options)
	{
		List<string> arguments = [];
		if (options is null || !options.Enabled)
			return arguments;

		bool wantsShaders = !string.IsNullOrWhiteSpace(options.AntiAliasing)
			|| options.Sharpening
			|| !string.IsNullOrWhiteSpace(options.GradingShader)
			|| !string.IsNullOrWhiteSpace(options.HomepageShader);

		if (wantsShaders && IsInstalled(ShaderLayerId))
		{
			arguments.Add("--filesystem=" + ConfigDirectory + ":ro");
			arguments.Add("--env=ENABLE_VKBASALT=1");
			arguments.Add("--env=VKBASALT_CONFIG_FILE=" + ConfigFile);
		}

		if (options.FrameGenMultiplier > 1 && IsInstalled(FrameGenLayerId))
		{
			arguments.Add("--env=ENABLE_LSFG=1");
			arguments.Add("--env=LSFG_MULTIPLIER=" + options.FrameGenMultiplier.ToString(CultureInfo.InvariantCulture));
		}

		return arguments;
	}
}
