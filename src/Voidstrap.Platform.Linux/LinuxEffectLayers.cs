using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

	public const string LayerBranch = "25.08";

	private const string Architecture = "x86_64";

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
		if (string.IsNullOrWhiteSpace(layerId))
			return false;

		try
		{
			if (LinuxFlatpakHost.IsSandboxed)
			{
				SystemProcessService processes = new();
				if (!LinuxFlatpakHost.TryCreateCommand(processes, ["info", "--runtime", layerId + "//" + LayerBranch], out ProcessCommand command))
					return false;
				OperationResult<ProcessExecution> result = processes.ExecuteAsync(command).GetAwaiter().GetResult();
				return result.Succeeded && result.Value is { ExitCode: 0 };
			}

			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string[] roots =
			[
				Path.Combine(home, ".local", "share", "flatpak", "runtime", layerId, Architecture, LayerBranch),
				Path.Combine("/var", "lib", "flatpak", "runtime", layerId, Architecture, LayerBranch)
			];

			foreach (string root in roots)
			{
				if (Directory.Exists(root))
					return true;
			}

			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static LinuxEffectLayerState GetState()
	{
		return new LinuxEffectLayerState(IsInstalled(ShaderLayerId), IsInstalled(FrameGenLayerId));
	}

	public static async Task<OperationResult> InstallAsync(IProcessService processes, string layerId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(processes);
		if (IsInstalled(layerId))
			return OperationResult.Success();

		if (!LinuxFlatpakHost.TryCreateCommand(processes, ["install", "--user", "--noninteractive", "flathub", layerId + "//" + LayerBranch], out ProcessCommand command))
			return OperationResult.Fail("FlatpakMissing", "Flatpak is not installed");

		OperationResult<ProcessExecution> result = await processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);

		if (!result.Succeeded || result.Value is null)
			return OperationResult.Fail("EffectLayerInstallFailed", result.Failure?.Message ?? "The effect layer could not be installed");

		if (result.Value.ExitCode != 0)
			return OperationResult.Fail("EffectLayerInstallFailed", "The effect layer installer reported an error");

		return IsInstalled(layerId)
			? OperationResult.Success()
			: OperationResult.Fail("EffectLayerInstallFailed", "The effect layer did not appear after installing");
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
			builder.AppendLine("toggleKey = Home");
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
