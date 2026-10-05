using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Voidstrap.Core;
using Voidstrap.Platform;

namespace Voidstrap.Platform.Linux;

internal static class LinuxEffectLayerFallback
{
	private const int OldestSourceYear = 23;

	private static readonly SemaphoreSlim InstallGate = new(1, 1);

	private static string ArchitectureName => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x86_64";

	private static string LibraryArchitecture => ArchitectureName == "aarch64" ? "aarch64-linux-gnu" : "x86_64-linux-gnu";

	private static string WorkRoot
	{
		get
		{
			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } custom ? custom : Path.Combine(home, ".cache");
			return Path.Combine(cache, "voidstrap", "effect-layers");
		}
	}

	public static void RemoveLegacyCopies()
	{
		try
		{
			string legacy = Path.Combine(LinuxEffectLayers.ConfigDirectory, "layers");
			if (Directory.Exists(legacy))
				Directory.Delete(legacy, true);
		}
		catch (Exception)
		{
		}
	}

	public static async Task<OperationResult> PrepareAsync(IProcessService processes, string layerId, CancellationToken cancellationToken)
	{
		if (!Supported(layerId))
			return OperationResult.Fail("EffectLayerUnavailable", "No compatible effect layer is available for Sober's runtime");

		string target = LinuxEffectLayers.LayerBranch;
		if (!TryParseBranch(target, out int targetYear))
			return OperationResult.Fail("EffectLayerUnavailable", "Sober's runtime branch " + target + " is not recognised");

		await InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		string work = Path.Combine(WorkRoot, layerId + "-" + target + "-" + Guid.NewGuid().ToString("N"));
		try
		{
			RemoveLegacyCopies();
			(string Branch, string Location)? source = await FindInstalledSourceAsync(processes, layerId, targetYear, cancellationToken).ConfigureAwait(false);
			if (source is null)
			{
				for (int year = targetYear - 1; year >= OldestSourceYear && source is null; year--)
				{
					string branch = year.ToString("00", CultureInfo.InvariantCulture) + ".08";
					OperationResult<ProcessExecution> install = await ExecuteAsync(processes,
						["install", "--user", "--noninteractive", "flathub", "runtime/" + layerId + "/" + ArchitectureName + "/" + branch], cancellationToken).ConfigureAwait(false);
					if (install.Succeeded && install.Value is { ExitCode: 0 })
						source = await FindInstalledSourceAsync(processes, layerId, targetYear, cancellationToken).ConfigureAwait(false);
				}
			}
			if (source is null)
				return OperationResult.Fail("EffectLayerUnavailable", "No published build of " + layerId + " could be installed from Flathub");

			string build = Path.Combine(work, "build");
			string files = Path.Combine(build, "files");
			Directory.CreateDirectory(build);
			OperationResult<ProcessExecution> copied = await ExecuteHostAsync(processes, "cp", ["-a", "--", Path.Combine(source.Value.Location, "files"), files], cancellationToken).ConfigureAwait(false);
			if (!copied.Succeeded || copied.Value is not { ExitCode: 0 })
				return OperationResult.Fail("EffectLayerUnavailable", "The effect layer files could not be copied: " + Details(copied));

			List<string> libraries = ExtensionLibraries(layerId, files);
			if (libraries.Count == 0)
				return OperationResult.Fail("EffectLayerUnavailable", "The published effect layer has no Vulkan manifest");

			File.WriteAllText(Path.Combine(build, "metadata"), BuildMetadata(layerId, target));

			string repository = Path.Combine(work, "repo");
			string bundle = Path.Combine(work, "layer.flatpak");
			OperationResult<ProcessExecution> exported = await ExecuteAsync(processes,
				["build-export", "--runtime", "--arch=" + ArchitectureName, "--disable-sandbox", "--subject=Voidstrap rebuild of " + layerId + " " + source.Value.Branch + " for " + target, repository, build, target], cancellationToken).ConfigureAwait(false);
			if (!exported.Succeeded || exported.Value is not { ExitCode: 0 })
				return OperationResult.Fail("EffectLayerUnavailable", "The effect layer could not be packaged for " + target + ": " + Details(exported));

			OperationResult<ProcessExecution> bundled = await ExecuteAsync(processes,
				["build-bundle", "--runtime", "--arch=" + ArchitectureName, repository, bundle, layerId, target], cancellationToken).ConfigureAwait(false);
			if (!bundled.Succeeded || bundled.Value is not { ExitCode: 0 })
				return OperationResult.Fail("EffectLayerUnavailable", "The effect layer bundle could not be created: " + Details(bundled));

			OperationResult<ProcessExecution> installed = await ExecuteAsync(processes,
				["install", "--user", "--noninteractive", "--reinstall", "--bundle", bundle], cancellationToken).ConfigureAwait(false);
			if (!installed.Succeeded || installed.Value is not { ExitCode: 0 })
				return OperationResult.Fail("EffectLayerUnavailable", "The effect layer could not be installed for " + target + ": " + Details(installed));

			OperationResult validated = await ValidateAsync(processes, libraries, cancellationToken).ConfigureAwait(false);
			if (!validated.Succeeded)
			{
				await ExecuteAsync(processes, ["uninstall", "--user", "--noninteractive", "runtime/" + layerId + "/" + ArchitectureName + "/" + target], CancellationToken.None).ConfigureAwait(false);
				return validated;
			}

			return OperationResult.Success();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			return OperationResult.Fail("EffectLayerUnavailable", "The compatible effect layer could not be prepared: " + exception.Message);
		}
		finally
		{
			try
			{
				if (Directory.Exists(work))
					Directory.Delete(work, true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			InstallGate.Release();
		}
	}

	private static bool Supported(string layerId) => layerId is LinuxEffectLayers.ShaderLayerId or LinuxEffectLayers.FrameGenLayerId or LinuxEffectLayers.MangoHudLayerId;

	private static bool TryParseBranch(string branch, out int year)
	{
		year = 0;
		return branch.Length == 5 && branch.EndsWith(".08", StringComparison.Ordinal)
			&& int.TryParse(branch[..2], NumberStyles.None, CultureInfo.InvariantCulture, out year);
	}

	private static string BuildMetadata(string layerId, string target)
	{
		string platform = "org.freedesktop.Platform/" + ArchitectureName + "/" + target;
		StringBuilder builder = new();
		builder.Append("[Runtime]\n");
		builder.Append("name=").Append(layerId).Append('\n');
		builder.Append("runtime=").Append(platform).Append('\n');
		builder.Append("sdk=org.freedesktop.Sdk/").Append(ArchitectureName).Append('/').Append(target).Append('\n');
		builder.Append('\n');
		builder.Append("[ExtensionOf]\n");
		builder.Append("ref=runtime/").Append(platform).Append('\n');
		builder.Append("runtime=").Append(platform).Append('\n');
		return builder.ToString();
	}

	private static List<string> ExtensionLibraries(string layerId, string files)
	{
		List<string> libraries = [];
		string prefix = "/usr/lib/extensions/vulkan/" + layerId[(layerId.LastIndexOf('.') + 1)..] + "/";
		foreach (string kind in new[] { "implicit_layer.d", "explicit_layer.d" })
		{
			string directory = Path.Combine(files, "share", "vulkan", kind);
			if (!Directory.Exists(directory))
				continue;
			foreach (string manifest in Directory.EnumerateFiles(directory, "*.json"))
			{
				string? library = JsonNode.Parse(File.ReadAllText(manifest))?["layer"]?["library_path"]?.GetValue<string>();
				if (library is null || !library.StartsWith(prefix, StringComparison.Ordinal))
					continue;
				library = library.Replace("$LIB", "lib/" + LibraryArchitecture, StringComparison.Ordinal);
				if (!libraries.Contains(library, StringComparer.Ordinal))
					libraries.Add(library);
			}
		}
		return libraries;
	}

	private static async Task<(string Branch, string Location)?> FindInstalledSourceAsync(IProcessService processes, string layerId, int targetYear, CancellationToken cancellationToken)
	{
		for (int year = targetYear - 1; year >= OldestSourceYear; year--)
		{
			string branch = year.ToString("00", CultureInfo.InvariantCulture) + ".08";
			foreach (string scope in new[] { "--user", "--system" })
			{
				OperationResult<ProcessExecution> result = await ExecuteAsync(processes,
					["info", scope, "--show-location", "runtime/" + layerId + "/" + ArchitectureName + "/" + branch], cancellationToken).ConfigureAwait(false);
				string path = result.Value?.StandardOutput.Trim() ?? string.Empty;
				if (result.Succeeded && result.Value is { ExitCode: 0 } && Path.IsPathFullyQualified(path) && !path.Any(char.IsControl))
					return (branch, path);
			}
		}
		return null;
	}

	private static async Task<OperationResult> ValidateAsync(IProcessService processes, IReadOnlyList<string> libraries, CancellationToken cancellationToken)
	{
		OperationResult<LinuxSoberRuntimeProvider.SoberFlatpakSelection> sober = await LinuxSoberRuntimeProvider.FindSoberInstallationAsync(processes, cancellationToken).ConfigureAwait(false);
		if (!sober.Succeeded || sober.Value is null)
			return OperationResult.Fail("EffectLayerIncompatible", sober.Failure?.Message ?? "Sober's runtime could not be queried");
		const string probe = """
			export LC_ALL=C
			for library do
				[ -f "$library" ] || { printf '%s is missing\n' "$library"; exit 1; }
				output=$(ldd -r "$library" 2>&1) || { printf '%s\n' "$output"; exit 1; }
				case "$output" in
					*"not found"*|*"undefined symbol"*) printf '%s\n' "$output"; exit 1 ;;
				esac
			done
			""";
		List<string> arguments = ["run", sober.Value.Scope, "--command=sh", sober.Value.Reference, "-c", probe, "voidstrap"];
		arguments.AddRange(libraries);
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));
		OperationResult<ProcessExecution> result = await ExecuteAsync(processes, arguments, timeout.Token).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		return result.Succeeded && result.Value is { ExitCode: 0 }
			? OperationResult.Success()
			: OperationResult.Fail("EffectLayerIncompatible", "The effect layer does not load in Sober's runtime: " + Details(result));
	}

	private static string Details(OperationResult<ProcessExecution> result)
	{
		if (result.Value is { } execution)
		{
			string text = (execution.StandardError.Trim() + "\n" + execution.StandardOutput.Trim()).Trim();
			string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (lines.Length > 0)
				return lines[^1];
		}
		return result.Failure?.Message ?? "no details";
	}

	private static async Task<OperationResult<ProcessExecution>> ExecuteAsync(IProcessService processes, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return await ExecuteHostAsync(processes, "flatpak", arguments, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<OperationResult<ProcessExecution>> ExecuteHostAsync(IProcessService processes, string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateHostCommand(processes, fileName, arguments, out ProcessCommand command))
			return OperationResult<ProcessExecution>.Fail("FlatpakMissing", fileName + " is not available");
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		return result;
	}
}
