using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using Voidstrap.Platform;

namespace Voidstrap.Platform.Linux;

public enum LinuxVinegarInstallationKind
{
	Native,
	Flatpak
}

public sealed record LinuxRuntimeConfigurationPaths(
	string ModsDirectory,
	string SoberConfigurationFile,
	string SoberAssetOverlayDirectory,
	string NativeVinegarStudioOverlayDirectory,
	string FlatpakVinegarStudioOverlayDirectory,
	string SoberAssetManifestFile,
	string NativeVinegarAssetManifestFile,
	string FlatpakVinegarAssetManifestFile,
	string NativeVinegarVersionsDirectory,
	string FlatpakVinegarVersionsDirectory)
{
	public string SoberFlagManifestFile => GetFlagManifestFile(SoberAssetManifestFile);

	public string NativeVinegarFlagManifestFile => GetFlagManifestFile(NativeVinegarAssetManifestFile);

	public string FlatpakVinegarFlagManifestFile => GetFlagManifestFile(FlatpakVinegarAssetManifestFile);

	public string NativeVinegarRefreshFile => GetRefreshFile(NativeVinegarAssetManifestFile);

	public string FlatpakVinegarRefreshFile => GetRefreshFile(FlatpakVinegarAssetManifestFile);

	public string NativeVinegarConfigurationFile => GetVinegarConfigurationFile(NativeVinegarStudioOverlayDirectory);

	public string FlatpakVinegarConfigurationFile => GetVinegarConfigurationFile(FlatpakVinegarStudioOverlayDirectory);

	public static LinuxRuntimeConfigurationPaths CreateDefault(string modsDirectory)
	{
		if (string.IsNullOrWhiteSpace(modsDirectory))
			throw new ArgumentException("The modifications directory is required", nameof(modsDirectory));

		string home = GetHomeDirectory();
		string configHome = GetXdgDirectory("XDG_CONFIG_HOME", home, ".config");
		string dataHome = GetXdgDirectory("XDG_DATA_HOME", home, ".local", "share");
		string stateHome = GetXdgDirectory("XDG_STATE_HOME", home, ".local", "state");
		string runtimeState = Path.Combine(stateHome, "voidstrap", "runtime");
		string soberRoot = Path.Combine(home, ".var", "app", "org.vinegarhq.Sober");
		string vinegarFlatpakRoot = Path.Combine(home, ".var", "app", "org.vinegarhq.Vinegar");

		return new LinuxRuntimeConfigurationPaths(
			Path.GetFullPath(modsDirectory),
			Path.Combine(soberRoot, "config", "sober", "config.json"),
			Path.Combine(soberRoot, "data", "sober", "asset_overlay"),
			Path.Combine(configHome, "vinegar", "overlays", "studio"),
			Path.Combine(vinegarFlatpakRoot, "config", "vinegar", "overlays", "studio"),
			Path.Combine(runtimeState, "sober.assets.json"),
			Path.Combine(runtimeState, "vinegar.native.assets.json"),
			Path.Combine(runtimeState, "vinegar.flatpak.assets.json"),
			Path.Combine(dataHome, "vinegar", "versions"),
			Path.Combine(vinegarFlatpakRoot, "data", "vinegar", "versions"));
	}

	private static string GetFlagManifestFile(string assetManifestFile)
	{
		const string suffix = ".assets.json";
		return assetManifestFile.EndsWith(suffix, StringComparison.Ordinal)
			? assetManifestFile[..^suffix.Length] + ".flags.json"
			: assetManifestFile + ".flags.json";
	}

	private static string GetRefreshFile(string assetManifestFile)
	{
		const string suffix = ".assets.json";
		return assetManifestFile.EndsWith(suffix, StringComparison.Ordinal)
			? assetManifestFile[..^suffix.Length] + ".refresh.json"
			: assetManifestFile + ".refresh.json";
	}

	private static string GetVinegarConfigurationFile(string studioOverlayDirectory)
	{
		DirectoryInfo? overlays = Directory.GetParent(Path.GetFullPath(studioOverlayDirectory));
		DirectoryInfo? configuration = overlays?.Parent;
		if (configuration is null)
			throw new InvalidOperationException("The Vinegar overlay path is invalid");
		return Path.Combine(configuration.FullName, "config.toml");
	}

	private static string GetHomeDirectory()
	{
		string? configured = Environment.GetEnvironmentVariable("HOME");
		if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
			return Path.GetFullPath(configured);

		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (!string.IsNullOrWhiteSpace(profile) && Path.IsPathRooted(profile))
			return Path.GetFullPath(profile);

		throw new DirectoryNotFoundException("The user home directory is unavailable");
	}

	private static string GetXdgDirectory(string variable, string home, params string[] fallbackSegments)
	{
		string? configured = Environment.GetEnvironmentVariable(variable);
		if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
			return Path.GetFullPath(configured);

		string path = home;
		foreach (string segment in fallbackSegments)
			path = Path.Combine(path, segment);
		return path;
	}
}

public sealed partial class LinuxRuntimeConfiguration
{
	private const string ClientSettingsRelativePath = "ClientSettings/ClientAppSettings.json";
	private const int LinuxCurrentWorkingDirectory = -100;
	private const int LinuxDoNotFollowLinks = 0x100;
	private const uint LinuxFileTypeMaskRequest = 0x1;
	private const ushort LinuxFileTypeMask = 0xf000;
	private const ushort LinuxRegularFileType = 0x8000;
	private static readonly SemaphoreSlim PreparationLock = new(1, 1);
	private static readonly Regex FlagNameExpression = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};
	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};
	private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
		? StringComparison.OrdinalIgnoreCase
		: StringComparison.Ordinal;
	private static readonly HashSet<string> ManagedSoberSettingKeys = new(StringComparer.Ordinal)
	{
		"allow_gamepad_permission",
		"close_on_leave",
		"discord_rpc_enabled",
		"discord_rpc_show_join_button",
		"enable_gamemode",
		"enable_hidpi",
		"enable_mobile_home_screen",
		"graphics_optimization_mode",
		"server_location_indicator_enabled",
		"touch_mode",
		"use_console_experience",
		"use_libsecret",
		"use_opengl"
	};

	private readonly LinuxRuntimeConfigurationPaths _paths;
	private readonly ISoberProcessProbe? _soberProcessProbe;
	private readonly SoberApkAssetIndexProvider? _soberAssetIndexProvider;

	[StructLayout(LayoutKind.Explicit, Size = 256)]
	private struct LinuxFileStatus
	{
		[FieldOffset(28)]
		public ushort Mode;
	}

	[LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int GetLinuxFileStatus(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxFileStatus status);

	public LinuxRuntimeConfiguration(LinuxRuntimeConfigurationPaths paths)
		: this(paths, null, null)
	{
	}

	public LinuxRuntimeConfiguration(
		LinuxRuntimeConfigurationPaths paths,
		ISoberProcessProbe? soberProcessProbe,
		SoberApkAssetIndexProvider? soberAssetIndexProvider)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_soberProcessProbe = soberProcessProbe;
		_soberAssetIndexProvider = soberAssetIndexProvider;
	}

	public static LinuxRuntimeConfiguration CreateDefault(string modsDirectory)
	{
		return new LinuxRuntimeConfiguration(LinuxRuntimeConfigurationPaths.CreateDefault(modsDirectory));
	}

	public static LinuxRuntimeConfiguration CreateDefault(string modsDirectory, IProcessService? processes)
	{
		return new LinuxRuntimeConfiguration(
			LinuxRuntimeConfigurationPaths.CreateDefault(modsDirectory),
			processes is null ? null : new LinuxSoberProcessProbe(processes),
			SoberApkAssetIndexProvider.CreateDefault());
	}

	public Task<OperationResult> PrepareAsync(RuntimeInstallation installation, CancellationToken cancellationToken = default)
	{
		return PrepareAsync(installation, null, cancellationToken);
	}

	public async Task<OperationResult> PrepareAsync(
		RuntimeInstallation installation,
		LinuxPlayerPreparationOptions? playerOptions,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(installation);

		if (installation.Kind == RuntimeKind.Player)
			return await PreparePlayerAsync(playerOptions ?? new LinuxPlayerPreparationOptions(), cancellationToken).ConfigureAwait(false);

		if (installation.Kind != RuntimeKind.Studio)
			return OperationResult.Fail("LinuxRuntimeKindInvalid", "The selected runtime type is not supported on Linux");

		LinuxVinegarInstallationKind kind;
		if (string.Equals(installation.Provider, "Vinegar Native", StringComparison.Ordinal))
			kind = LinuxVinegarInstallationKind.Native;
		else if (string.Equals(installation.Provider, "Vinegar Flatpak", StringComparison.Ordinal))
			kind = LinuxVinegarInstallationKind.Flatpak;
		else
			return OperationResult.Fail("VinegarProviderInvalid", "The selected Vinegar installation is not supported");

		return await PrepareStudioAsync(kind, cancellationToken).ConfigureAwait(false);
	}

	public Task<OperationResult> PreparePlayerAsync(CancellationToken cancellationToken = default)
	{
		return PreparePlayerAsync(new LinuxPlayerPreparationOptions(), cancellationToken);
	}

	public async Task<OperationResult> PreparePlayerAsync(LinuxPlayerPreparationOptions options, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);

		await PreparationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			OperationResult configResult = await MergeSoberConfigurationAsync(options, cancellationToken).ConfigureAwait(false);
			if (!configResult.Succeeded)
				return configResult;

			SoberApkAssetIndex? assetIndex = null;
			if (_soberAssetIndexProvider is not null)
			{
				OperationResult<SoberApkAssetIndex> indexResult = await _soberAssetIndexProvider.LoadAsync(cancellationToken).ConfigureAwait(false);
				if (indexResult.Succeeded)
					assetIndex = indexResult.Value;
			}

			return await SynchronizeAssetsAsync(
				_paths.SoberAssetOverlayDirectory,
				_paths.SoberAssetManifestFile,
				static relative => string.Equals(relative, ClientSettingsRelativePath, StringComparison.OrdinalIgnoreCase),
				cancellationToken,
				assetIndex: assetIndex).ConfigureAwait(false);
		}
		finally
		{
			PreparationLock.Release();
		}
	}

	public async Task<OperationResult> PrepareStudioAsync(LinuxVinegarInstallationKind kind, CancellationToken cancellationToken = default)
	{
		string targetDirectory = kind == LinuxVinegarInstallationKind.Native
			? _paths.NativeVinegarStudioOverlayDirectory
			: _paths.FlatpakVinegarStudioOverlayDirectory;
		string manifestFile = kind == LinuxVinegarInstallationKind.Native
			? _paths.NativeVinegarAssetManifestFile
			: _paths.FlatpakVinegarAssetManifestFile;
		string configurationFile = kind == LinuxVinegarInstallationKind.Native
			? _paths.NativeVinegarConfigurationFile
			: _paths.FlatpakVinegarConfigurationFile;
		string flagManifestFile = kind == LinuxVinegarInstallationKind.Native
			? _paths.NativeVinegarFlagManifestFile
			: _paths.FlatpakVinegarFlagManifestFile;
		string versionsDirectory = kind == LinuxVinegarInstallationKind.Native
			? _paths.NativeVinegarVersionsDirectory
			: _paths.FlatpakVinegarVersionsDirectory;

		await PreparationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			OperationResult flagsResult = await MergeVinegarFlagsAsync(configurationFile, flagManifestFile, cancellationToken).ConfigureAwait(false);
			if (!flagsResult.Succeeded)
				return flagsResult;

			return await SynchronizeAssetsAsync(
				targetDirectory,
				manifestFile,
				static relative => string.Equals(relative, ClientSettingsRelativePath, StringComparison.OrdinalIgnoreCase),
				cancellationToken,
				versionsDirectory).ConfigureAwait(false);
		}
		finally
		{
			PreparationLock.Release();
		}
	}

	private async Task<OperationResult> MergeSoberConfigurationAsync(LinuxPlayerPreparationOptions options, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			JsonObject sourceFlags;
			if (options.UseFastFlagManager)
			{
				OperationResult<JsonObject> sourceResult = await ReadClientFlagsAsync(cancellationToken).ConfigureAwait(false);
				if (!sourceResult.Succeeded || sourceResult.Value is null)
					return OperationResult.Fail(sourceResult.Failure!.Code, sourceResult.Failure.Message, sourceResult.Failure.State);
				sourceFlags = sourceResult.Value;
			}
			else
			{
				sourceFlags = new JsonObject();
			}

			OperationResult<Dictionary<string, JsonNode?>> settingsResult = BuildNativeSettings(options.NativeConfiguration);
			if (!settingsResult.Succeeded || settingsResult.Value is null)
				return OperationResult.Fail(settingsResult.Failure!.Code, settingsResult.Failure.Message, settingsResult.Failure.State);
			Dictionary<string, JsonNode?> sourceSettings = settingsResult.Value;

			OperationResult initialFlagManifestSafety = EnsureManifestSafety(_paths.SoberFlagManifestFile);
			if (!initialFlagManifestSafety.Succeeded)
				return initialFlagManifestSafety;
			OperationResult<SoberOwnershipManifest> previousResult = await ReadFlagManifestAsync(_paths.SoberFlagManifestFile, cancellationToken).ConfigureAwait(false);
			if (!previousResult.Succeeded || previousResult.Value is null)
				return OperationResult.Fail(previousResult.Failure!.Code, previousResult.Failure.Message, previousResult.Failure.State);
			HashSet<string> previous = previousResult.Value.Flags;
			HashSet<string> previousSettings = previousResult.Value.Settings;
			HashSet<string> current = new(sourceFlags.Select(static pair => pair.Key), StringComparer.Ordinal);
			HashSet<string> currentSettings = new(sourceSettings.Keys, StringComparer.Ordinal);
			if (current.Count == 0 && previous.Count == 0 && currentSettings.Count == 0 && previousSettings.Count == 0)
				return OperationResult.Success();

			if (_soberProcessProbe is not null && await _soberProcessProbe.IsRunningAsync(cancellationToken).ConfigureAwait(false))
			{
				return OperationResult.Fail(
					"SoberRunning",
					"Sober is already running and would overwrite the configuration when it exits. Close Sober, then retry the launch.",
					CapabilityState.RequiresExternalRuntime);
			}

			string configurationFile = Path.GetFullPath(_paths.SoberConfigurationFile);
			string? configurationDirectory = Path.GetDirectoryName(configurationFile);
			if (string.IsNullOrWhiteSpace(configurationDirectory))
				return OperationResult.Fail("SoberConfigurationPathInvalid", "The Sober configuration path is invalid");

			OperationResult directorySafety = EnsureDirectorySafety(configurationDirectory);
			if (!directorySafety.Succeeded)
				return directorySafety;
			OperationResult fileSafety = ValidateDestinationFileSafety(configurationDirectory, configurationFile);
			if (!fileSafety.Succeeded)
				return fileSafety;

			JsonObject soberConfiguration;
			string header = string.Empty;
			if (File.Exists(configurationFile))
			{
				string existing = await File.ReadAllTextAsync(configurationFile, cancellationToken).ConfigureAwait(false);
				header = ExtractLeadingComments(existing);
				JsonNode? configurationNode = ParseJsonNode(existing);
				if (configurationNode is not JsonObject configurationObject)
					return OperationResult.Fail("SoberConfigurationInvalid", "The Sober configuration must contain a JSON object");
				soberConfiguration = configurationObject;
			}
			else
			{
				soberConfiguration = new JsonObject();
			}

			JsonObject soberFlags;
			if (soberConfiguration["fflags"] is null)
			{
				soberFlags = new JsonObject();
				soberConfiguration["fflags"] = soberFlags;
			}
			else if (soberConfiguration["fflags"] is JsonObject existingFlags)
			{
				soberFlags = existingFlags;
			}
			else
			{
				return OperationResult.Fail("SoberFlagsSectionInvalid", "The Sober fflags setting must contain a JSON object");
			}

			foreach (string staleName in previous.Except(current, StringComparer.Ordinal))
				soberFlags.Remove(staleName);

			foreach ((string name, JsonNode? value) in sourceFlags)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!IsSafeFlagName(name))
					return OperationResult.Fail("SoberFlagNameInvalid", "Client settings contain an invalid flag name");
				OperationResult<JsonNode?> converted = ConvertFlagValue(name, value);
				if (!converted.Succeeded)
					return OperationResult.Fail(converted.Failure!.Code, converted.Failure.Message, converted.Failure.State);
				soberFlags[name] = converted.Value;
			}

			if (soberFlags.Count == 0 && soberConfiguration["fflags"] is JsonObject)
				soberConfiguration.Remove("fflags");

			foreach (string staleSetting in previousSettings.Except(currentSettings, StringComparer.Ordinal))
				soberConfiguration.Remove(staleSetting);

			foreach ((string key, JsonNode? value) in sourceSettings)
			{
				cancellationToken.ThrowIfCancellationRequested();
				soberConfiguration[key] = value;
			}

			HashSet<string> transactionManifest = new(previous, StringComparer.Ordinal);
			transactionManifest.UnionWith(current);
			HashSet<string> transactionSettings = new(previousSettings, StringComparer.Ordinal);
			transactionSettings.UnionWith(currentSettings);
			await WriteFlagManifestAsync(_paths.SoberFlagManifestFile, transactionManifest, transactionSettings, cancellationToken).ConfigureAwait(false);
			await WriteTextAtomicallyAsync(
				configurationFile,
				header + soberConfiguration.ToJsonString(JsonOptions) + "\n",
				cancellationToken,
				false).ConfigureAwait(false);
			await WriteFlagManifestAsync(_paths.SoberFlagManifestFile, current, currentSettings, cancellationToken).ConfigureAwait(false);
			return OperationResult.Success();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (JsonException ex)
		{
			return OperationResult.Fail("SoberConfigurationInvalid", "Sober runtime configuration failed: " + ex.Message);
		}
		catch (Exception ex)
		{
			return OperationResult.Fail("SoberConfigurationFailed", "Sober runtime configuration failed: " + ex.Message);
		}
	}

	private async Task<OperationResult<JsonObject>> ReadClientFlagsAsync(CancellationToken cancellationToken)
	{
		string sourceFile = Path.Combine(_paths.ModsDirectory, "ClientSettings", "ClientAppSettings.json");
		if (!File.Exists(sourceFile))
			return OperationResult<JsonObject>.Success(new JsonObject());

		OperationResult sourceSafety = ValidateExistingFileSafety(_paths.ModsDirectory, sourceFile);
		if (!sourceSafety.Succeeded)
			return OperationResult<JsonObject>.Fail(sourceSafety.Failure!.Code, sourceSafety.Failure.Message, sourceSafety.Failure.State);

		JsonNode? sourceNode = await ReadJsonNodeAsync(sourceFile, cancellationToken).ConfigureAwait(false);
		return sourceNode is JsonObject sourceFlags
			? OperationResult<JsonObject>.Success(sourceFlags)
			: OperationResult<JsonObject>.Fail("ClientSettingsInvalid", "Client settings must contain a JSON object");
	}

	private async Task<OperationResult> MergeVinegarFlagsAsync(string configurationFile, string manifestFile, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			OperationResult<JsonObject> sourceResult = await ReadClientFlagsAsync(cancellationToken).ConfigureAwait(false);
			if (!sourceResult.Succeeded || sourceResult.Value is null)
				return OperationResult.Fail(sourceResult.Failure!.Code, sourceResult.Failure.Message, sourceResult.Failure.State);
			JsonObject sourceFlags = sourceResult.Value;
			OperationResult initialManifestSafety = EnsureManifestSafety(manifestFile);
			if (!initialManifestSafety.Succeeded)
				return initialManifestSafety;
			OperationResult<SoberOwnershipManifest> previousResult = await ReadFlagManifestAsync(manifestFile, cancellationToken).ConfigureAwait(false);
			if (!previousResult.Succeeded || previousResult.Value is null)
				return OperationResult.Fail(previousResult.Failure!.Code, previousResult.Failure.Message, previousResult.Failure.State);
			HashSet<string> previous = previousResult.Value.Flags;
			HashSet<string> current = new(sourceFlags.Select(static pair => pair.Key), StringComparer.Ordinal);
			if (current.Count == 0 && previous.Count == 0)
				return OperationResult.Success();

			string fullConfigurationFile = Path.GetFullPath(configurationFile);
			string? configurationDirectory = Path.GetDirectoryName(fullConfigurationFile);
			if (string.IsNullOrWhiteSpace(configurationDirectory))
				return OperationResult.Fail("VinegarConfigurationPathInvalid", "The Vinegar configuration path is invalid");
			OperationResult directorySafety = EnsureDirectorySafety(configurationDirectory);
			if (!directorySafety.Succeeded)
				return directorySafety;
			OperationResult fileSafety = ValidateDestinationFileSafety(configurationDirectory, fullConfigurationFile);
			if (!fileSafety.Succeeded)
				return fileSafety;

			Dictionary<string, string> serializedFlags = new(StringComparer.Ordinal);
			foreach ((string name, JsonNode? value) in sourceFlags)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!IsSafeFlagName(name))
					return OperationResult.Fail("VinegarFlagNameInvalid", "Client settings contain an invalid flag name");
				OperationResult<JsonNode?> converted = ConvertFlagValue(name, value);
				if (!converted.Succeeded)
					return OperationResult.Fail(converted.Failure!.Code, converted.Failure.Message, converted.Failure.State);
				OperationResult<string> formatted = FormatTomlValue(converted.Value);
				if (!formatted.Succeeded || formatted.Value is null)
					return OperationResult.Fail(formatted.Failure!.Code, formatted.Failure.Message, formatted.Failure.State);
				serializedFlags[name] = formatted.Value;
			}

			string existing = File.Exists(fullConfigurationFile)
				? await File.ReadAllTextAsync(fullConfigurationFile, cancellationToken).ConfigureAwait(false)
				: string.Empty;
			OperationResult<string> merged = MergeVinegarToml(existing, serializedFlags, previous);
			if (!merged.Succeeded || merged.Value is null)
				return OperationResult.Fail(merged.Failure!.Code, merged.Failure.Message, merged.Failure.State);

			HashSet<string> transactionManifest = new(previous, StringComparer.Ordinal);
			transactionManifest.UnionWith(current);
			await WriteFlagManifestAsync(manifestFile, transactionManifest, cancellationToken).ConfigureAwait(false);
			await WriteTextAtomicallyAsync(fullConfigurationFile, merged.Value, cancellationToken, false).ConfigureAwait(false);
			await WriteFlagManifestAsync(manifestFile, current, cancellationToken).ConfigureAwait(false);
			return OperationResult.Success();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (JsonException ex)
		{
			return OperationResult.Fail("VinegarConfigurationInvalid", "Vinegar runtime configuration failed: " + ex.Message);
		}
		catch (Exception ex)
		{
			return OperationResult.Fail("VinegarConfigurationFailed", "Vinegar runtime configuration failed: " + ex.Message);
		}
	}

	private static OperationResult<string> MergeVinegarToml(string existing, IReadOnlyDictionary<string, string> current, IReadOnlySet<string> previous)
	{
		try
		{
			string lineEnding = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
			DocumentSyntax document = SyntaxParser.ParseStrict(existing);
			TableSyntax? section = null;
			foreach (TableSyntaxBase candidate in document.Tables)
			{
				if (candidate is not TableSyntax table || !IsStudioFlagsTable(table))
					continue;
				if (section is not null)
					return OperationResult<string>.Fail("VinegarConfigurationInvalid", "The Vinegar configuration contains more than one Studio fflags table");
				section = table;
			}

			if (section is null)
			{
				DocumentSyntax tableDocument = SyntaxParser.ParseStrict("[studio.fflags]" + lineEnding);
				section = tableDocument.Tables.GetChild(0) as TableSyntax;
				if (section is null)
					return OperationResult<string>.Fail("VinegarConfigurationInvalid", "The Vinegar Studio fflags table could not be created");
				tableDocument.Tables.RemoveChild(section);
				document.Tables.Add(section);
			}

			HashSet<string> managed = new(previous, StringComparer.Ordinal);
			managed.UnionWith(current.Keys);
			foreach (KeyValueSyntax item in section.Items.ToList())
			{
				IReadOnlyList<string>? parts = GetTomlKeyParts(item.Key);
				if (parts is { Count: 1 } && managed.Contains(parts[0]))
					section.Items.RemoveChild(item);
			}
			if (current.Count > 0)
			{
				if (section.Items.ChildrenCount > 0)
				{
					KeyValueSyntax? finalItem = section.Items.GetChild(section.Items.ChildrenCount - 1);
					if (finalItem is not null)
						finalItem.EndOfLineToken = CreateTomlLineEnding(finalItem.EndOfLineToken, lineEnding);
				}
				else
				{
					section.EndOfLineToken = CreateTomlLineEnding(section.EndOfLineToken, lineEnding);
				}
			}

			foreach ((string name, string value) in current.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
			{
				DocumentSyntax itemDocument = SyntaxParser.ParseStrict(QuoteTomlString(name) + " = " + value + lineEnding);
				KeyValueSyntax? item = itemDocument.KeyValues.GetChild(0);
				if (item is null)
					return OperationResult<string>.Fail("VinegarConfigurationInvalid", "A Vinegar Studio flag could not be created");
				itemDocument.KeyValues.RemoveChild(item);
				item.EndOfLineToken = new SyntaxToken(TokenKind.NewLine, lineEnding);
				section.Items.Add(item);
			}

			return OperationResult<string>.Success(document.ToString());
		}
		catch (TomlException ex)
		{
			return OperationResult<string>.Fail("VinegarConfigurationInvalid", "The Vinegar configuration is invalid: " + ex.Message);
		}
	}

	private static SyntaxToken CreateTomlLineEnding(SyntaxToken? previous, string lineEnding)
	{
		SyntaxToken token = new(TokenKind.NewLine, lineEnding)
		{
			LeadingTrivia = previous?.LeadingTrivia,
			TrailingTrivia = previous?.TrailingTrivia
		};
		if (previous is not null)
		{
			previous.LeadingTrivia = null;
			previous.TrailingTrivia = null;
		}
		return token;
	}

	private static bool IsStudioFlagsTable(TableSyntax table)
	{
		IReadOnlyList<string>? parts = GetTomlKeyParts(table.Name);
		return parts is { Count: 2 }
			&& string.Equals(parts[0], "studio", StringComparison.Ordinal)
			&& string.Equals(parts[1], "fflags", StringComparison.Ordinal);
	}

	private static IReadOnlyList<string>? GetTomlKeyParts(KeySyntax? key)
	{
		if (key is null || GetTomlKeyPart(key.Key) is not string first)
			return null;
		List<string> parts = [first];
		foreach (DottedKeyItemSyntax item in key.DotKeys)
		{
			string? part = GetTomlKeyPart(item.Key);
			if (part is null)
				return null;
			parts.Add(part);
		}
		return parts;
	}

	private static string? GetTomlKeyPart(BareKeyOrStringValueSyntax? key)
	{
		return key switch
		{
			BareKeySyntax bare => bare.Key?.Text,
			StringValueSyntax quoted => quoted.Value,
			_ => null
		};
	}

	private static OperationResult<string> FormatTomlValue(JsonNode? value)
	{
		if (value is not JsonValue jsonValue)
			return OperationResult<string>.Fail("VinegarFlagValueInvalid", "Client settings contain a flag value that Vinegar cannot use");
		return jsonValue.GetValueKind() switch
		{
			JsonValueKind.True => OperationResult<string>.Success("true"),
			JsonValueKind.False => OperationResult<string>.Success("false"),
			JsonValueKind.Number => OperationResult<string>.Success(jsonValue.ToJsonString()),
			JsonValueKind.String => OperationResult<string>.Success(QuoteTomlString(jsonValue.GetValue<string>())),
			_ => OperationResult<string>.Fail("VinegarFlagValueInvalid", "Client settings contain a flag value that Vinegar cannot use")
		};
	}

	private static string QuoteTomlString(string value)
	{
		StringBuilder builder = new(value.Length + 2);
		builder.Append('"');
		foreach (char character in value)
		{
			builder.Append(character switch
			{
				'"' => "\\\"",
				'\\' => "\\\\",
				'\b' => "\\b",
				'\t' => "\\t",
				'\n' => "\\n",
				'\f' => "\\f",
				'\r' => "\\r",
				_ when char.IsControl(character) => "\\u" + ((int)character).ToString("X4", CultureInfo.InvariantCulture),
				_ => character.ToString()
			});
		}
		builder.Append('"');
		return builder.ToString();
	}

	private static async Task<OperationResult<SoberOwnershipManifest>> ReadFlagManifestAsync(string manifestFile, CancellationToken cancellationToken)
	{
		HashSet<string> names = new(StringComparer.Ordinal);
		HashSet<string> settings = new(StringComparer.Ordinal);
		if (!File.Exists(manifestFile))
			return OperationResult<SoberOwnershipManifest>.Success(new SoberOwnershipManifest(names, settings));

		JsonNode? node = await ReadJsonNodeAsync(manifestFile, cancellationToken).ConfigureAwait(false);
		if (node is not JsonObject manifest || manifest["names"] is not JsonArray entries)
			return OperationResult<SoberOwnershipManifest>.Fail("LinuxFlagManifestInvalid", "The managed flag manifest must contain a names array");
		foreach (JsonNode? entry in entries)
		{
			if (entry is not JsonValue value || !value.TryGetValue(out string? name) || !IsSafeFlagName(name))
				return OperationResult<SoberOwnershipManifest>.Fail("LinuxFlagManifestInvalid", "The managed flag manifest contains an invalid flag name");
			names.Add(name);
		}

		if (manifest["settings"] is JsonNode settingsNode)
		{
			if (settingsNode is not JsonArray settingsEntries)
				return OperationResult<SoberOwnershipManifest>.Fail("LinuxFlagManifestInvalid", "The managed flag manifest must contain a settings array");
			foreach (JsonNode? entry in settingsEntries)
			{
				if (entry is not JsonValue value || !value.TryGetValue(out string? key) || key is null || !ManagedSoberSettingKeys.Contains(key))
					return OperationResult<SoberOwnershipManifest>.Fail("LinuxFlagManifestInvalid", "The managed flag manifest contains an unmanaged Sober setting");
				settings.Add(key);
			}
		}

		return OperationResult<SoberOwnershipManifest>.Success(new SoberOwnershipManifest(names, settings));
	}

	private static Task WriteFlagManifestAsync(string manifestFile, IEnumerable<string> names, CancellationToken cancellationToken)
	{
		return WriteFlagManifestAsync(manifestFile, names, [], cancellationToken);
	}

	private static Task WriteFlagManifestAsync(string manifestFile, IEnumerable<string> names, IEnumerable<string> settings, CancellationToken cancellationToken)
	{
		JsonArray entries = new(names.Order(StringComparer.Ordinal).Select(static name => (JsonNode?)JsonValue.Create(name)).ToArray());
		JsonArray settingEntries = new(settings.Order(StringComparer.Ordinal).Select(static key => (JsonNode?)JsonValue.Create(key)).ToArray());
		JsonObject manifest = new()
		{
			["names"] = entries,
			["settings"] = settingEntries
		};
		return WriteJsonAtomicallyAsync(manifestFile, manifest, cancellationToken, true);
	}

	private static OperationResult<Dictionary<string, JsonNode?>> BuildNativeSettings(SoberNativeConfigurationOptions? options)
	{
		Dictionary<string, JsonNode?> settings = new(StringComparer.Ordinal);
		if (options is null)
			return OperationResult<Dictionary<string, JsonNode?>>.Success(settings);

		AddNativeSetting(settings, "allow_gamepad_permission", options.AllowGamepadPermission);
		AddNativeSetting(settings, "close_on_leave", options.CloseOnLeave);
		AddNativeSetting(settings, "discord_rpc_enabled", options.DiscordRpcEnabled);
		AddNativeSetting(settings, "discord_rpc_show_join_button", options.DiscordRpcShowJoinButton);
		AddNativeSetting(settings, "enable_gamemode", options.EnableGameMode);
		AddNativeSetting(settings, "enable_hidpi", options.EnableHiDpi);
		AddNativeSetting(settings, "enable_mobile_home_screen", options.EnableMobileHomeScreen);
		AddNativeSetting(settings, "server_location_indicator_enabled", options.ServerLocationIndicatorEnabled);
		AddNativeSetting(settings, "use_console_experience", options.UseConsoleExperience);
		AddNativeSetting(settings, "use_libsecret", options.UseLibsecret);
		AddNativeSetting(settings, "use_opengl", options.UseOpenGl);

		if (options.GraphicsOptimizationMode is SoberGraphicsOptimizationMode graphics)
		{
			string? value = graphics switch
			{
				SoberGraphicsOptimizationMode.Quality => "quality",
				SoberGraphicsOptimizationMode.Balanced => "balanced",
				SoberGraphicsOptimizationMode.Performance => "performance",
				_ => null
			};
			if (value is null)
				return OperationResult<Dictionary<string, JsonNode?>>.Fail("SoberSettingValueInvalid", "The selected Sober graphics mode is not supported");
			settings["graphics_optimization_mode"] = JsonValue.Create(value);
		}

		if (options.TouchMode is SoberTouchMode touch)
		{
			string? value = touch switch
			{
				SoberTouchMode.Off => "off",
				SoberTouchMode.On => "on",
				SoberTouchMode.FakeOff => "fake-off",
				_ => null
			};
			if (value is null)
				return OperationResult<Dictionary<string, JsonNode?>>.Fail("SoberSettingValueInvalid", "The selected Sober touch mode is not supported");
			settings["touch_mode"] = JsonValue.Create(value);
		}

		return OperationResult<Dictionary<string, JsonNode?>>.Success(settings);
	}

	private static void AddNativeSetting(Dictionary<string, JsonNode?> settings, string key, bool? value)
	{
		if (value is bool resolved)
			settings[key] = JsonValue.Create(resolved);
	}

	private static bool IsSafeFlagName(string? name)
	{
		return !string.IsNullOrWhiteSpace(name) && FlagNameExpression.IsMatch(name);
	}

	private static OperationResult EnsureManifestSafety(string manifestFile)
	{
		string manifestPath = Path.GetFullPath(manifestFile);
		string? directory = Path.GetDirectoryName(manifestPath);
		if (string.IsNullOrWhiteSpace(directory))
			return OperationResult.Fail("LinuxManifestPathInvalid", "The managed runtime manifest path is invalid");
		OperationResult directorySafety = EnsureDirectorySafety(directory);
		if (!directorySafety.Succeeded)
			return directorySafety;
		return ValidateDestinationFileSafety(directory, manifestPath);
	}

	private async Task<OperationResult> SynchronizeAssetsAsync(
		string targetDirectory,
		string manifestFile,
		Func<string, bool> exclude,
		CancellationToken cancellationToken,
		string? vinegarVersionsDirectory = null,
		SoberApkAssetIndex? assetIndex = null)
	{
		List<StagedAsset> staged = [];
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			string sourceRoot = Path.GetFullPath(_paths.ModsDirectory);
			string targetRoot = Path.GetFullPath(targetDirectory);
			string manifestPath = Path.GetFullPath(manifestFile);
			if (IsContainedPath(targetRoot, manifestPath) || IsContainedPath(sourceRoot, manifestPath))
				return OperationResult.Fail("LinuxAssetManifestPathInvalid", "The managed asset manifest must be stored outside the source and overlay directories");

			OperationResult sourceSafety = ValidateSourceRootSafety(sourceRoot);
			if (!sourceSafety.Succeeded)
				return sourceSafety;
			OperationResult targetSafety = EnsureDirectorySafety(targetRoot);
			if (!targetSafety.Succeeded)
				return targetSafety;

			string? manifestDirectory = Path.GetDirectoryName(manifestPath);
			if (string.IsNullOrWhiteSpace(manifestDirectory))
				return OperationResult.Fail("LinuxAssetManifestPathInvalid", "The managed asset manifest path is invalid");
			OperationResult manifestDirectorySafety = EnsureDirectorySafety(manifestDirectory);
			if (!manifestDirectorySafety.Succeeded)
				return manifestDirectorySafety;
			OperationResult manifestSafety = ValidateDestinationFileSafety(manifestDirectory, manifestPath);
			if (!manifestSafety.Succeeded)
				return manifestSafety;

			OperationResult<HashSet<string>> previousResult = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
			if (!previousResult.Succeeded || previousResult.Value is null)
				return OperationResult.Fail(previousResult.Failure!.Code, previousResult.Failure.Message, previousResult.Failure.State);
			HashSet<string> previous = previousResult.Value;

			OperationResult<List<SourceAsset>> sourceResult = EnumerateSourceAssets(sourceRoot, exclude, cancellationToken);
			if (!sourceResult.Succeeded || sourceResult.Value is null)
				return OperationResult.Fail(sourceResult.Failure!.Code, sourceResult.Failure.Message, sourceResult.Failure.State);
			List<SourceAsset> sourceAssets = sourceResult.Value;
			if (assetIndex is not null)
			{
				OperationResult<List<SourceAsset>> mappedResult = MapToPackageAssets(sourceAssets, assetIndex);
				if (!mappedResult.Succeeded || mappedResult.Value is null)
					return OperationResult.Fail(mappedResult.Failure!.Code, mappedResult.Failure.Message, mappedResult.Failure.State);
				sourceAssets = mappedResult.Value;
			}

			HashSet<string> current = new(sourceAssets.Select(asset => asset.RelativePath), StringComparer.Ordinal);
			List<SourceAsset> changedAssets = [];

			foreach (SourceAsset asset in sourceAssets)
			{
				cancellationToken.ThrowIfCancellationRequested();
				OperationResult<string> destinationResult = ResolveContainedPath(targetRoot, asset.RelativePath);
				if (!destinationResult.Succeeded || destinationResult.Value is null)
					return OperationResult.Fail(destinationResult.Failure!.Code, destinationResult.Failure.Message, destinationResult.Failure.State);
				string destination = destinationResult.Value;
				OperationResult destinationSafety = ValidateDestinationFileSafety(targetRoot, destination);
				if (!destinationSafety.Succeeded)
					return destinationSafety;
				if (Directory.Exists(destination))
					return OperationResult.Fail("LinuxAssetDestinationInvalid", "A managed asset path conflicts with an existing directory");
				if (File.Exists(destination) && !previous.Contains(asset.RelativePath))
					return OperationResult.Fail("LinuxAssetConflict", "A user owned overlay file conflicts with a managed modification");
				if (!File.Exists(destination) || !previous.Contains(asset.RelativePath) || !await FilesEqualAsync(asset.SourcePath, destination, cancellationToken).ConfigureAwait(false))
					changedAssets.Add(asset);
			}

			foreach (string staleRelativePath in previous.Except(current, StringComparer.Ordinal))
			{
				OperationResult<string> staleResult = ResolveContainedPath(targetRoot, staleRelativePath);
				if (!staleResult.Succeeded || staleResult.Value is null)
					return OperationResult.Fail(staleResult.Failure!.Code, staleResult.Failure.Message, staleResult.Failure.State);
				OperationResult staleSafety = ValidateDestinationFileSafety(targetRoot, staleResult.Value);
				if (!staleSafety.Succeeded)
					return staleSafety;
			}

			if (!string.IsNullOrWhiteSpace(vinegarVersionsDirectory))
			{
				string refreshFile = GetRefreshFileForManifest(manifestPath);
				OperationResult refreshSafety = EnsureManifestSafety(refreshFile);
				if (!refreshSafety.Succeeded)
					return refreshSafety;
				OperationResult pendingRefresh = await ResolvePendingVinegarRefreshAsync(refreshFile, vinegarVersionsDirectory, cancellationToken).ConfigureAwait(false);
				if (!pendingRefresh.Succeeded)
					return pendingRefresh;

				bool requiresRefresh = !previous.SetEquals(current) || changedAssets.Count > 0;

				if (requiresRefresh)
				{
					OperationResult<List<string>> deploymentsResult = GetVinegarDeployments(vinegarVersionsDirectory);
					if (!deploymentsResult.Succeeded || deploymentsResult.Value is null)
						return OperationResult.Fail(deploymentsResult.Failure!.Code, deploymentsResult.Failure.Message, deploymentsResult.Failure.State);
					if (deploymentsResult.Value.Count > 0)
					{
						await WriteVinegarRefreshAsync(refreshFile, deploymentsResult.Value, cancellationToken).ConfigureAwait(false);
						return OperationResult.Fail(
							"VinegarRefreshRequired",
							"Vinegar must refresh its installed Studio files before changed modifications can take effect. Open Vinegar Manage, choose Uninstall Studio, then retry the launch.",
							CapabilityState.RequiresExternalRuntime);
					}
				}
			}

			if (changedAssets.Count == 0 && previous.SetEquals(current))
				return OperationResult.Success();

			foreach (SourceAsset asset in changedAssets)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string destination = ResolveContainedPath(targetRoot, asset.RelativePath).Value!;
				string? destinationParent = Path.GetDirectoryName(destination);
				if (string.IsNullOrWhiteSpace(destinationParent))
					return OperationResult.Fail("LinuxAssetDestinationInvalid", "A managed asset destination is invalid");
				OperationResult parentSafety = EnsureContainedDirectorySafety(targetRoot, destinationParent);
				if (!parentSafety.Succeeded)
					return parentSafety;
				string stagedFile = Path.Combine(destinationParent, ".voidstrap." + Guid.NewGuid().ToString("N") + ".tmp");
				await CopyFileAsync(asset.SourcePath, stagedFile, cancellationToken).ConfigureAwait(false);
				staged.Add(new StagedAsset(stagedFile, destination));
			}

			HashSet<string> transactionManifest = new(previous, StringComparer.Ordinal);
			transactionManifest.UnionWith(current);
			await WriteManifestAsync(manifestPath, transactionManifest, cancellationToken).ConfigureAwait(false);

			foreach (StagedAsset asset in staged)
			{
				cancellationToken.ThrowIfCancellationRequested();
				File.Move(asset.StagedPath, asset.DestinationPath, true);
			}

			foreach (string staleRelativePath in previous.Except(current, StringComparer.Ordinal))
			{
				cancellationToken.ThrowIfCancellationRequested();
				string stalePath = ResolveContainedPath(targetRoot, staleRelativePath).Value!;
				if (File.Exists(stalePath))
					File.Delete(stalePath);
				RemoveEmptyParents(targetRoot, Path.GetDirectoryName(stalePath));
			}

			await WriteManifestAsync(manifestPath, current, cancellationToken).ConfigureAwait(false);
			return OperationResult.Success();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (JsonException ex)
		{
			return OperationResult.Fail("LinuxAssetManifestInvalid", "The managed asset manifest is invalid: " + ex.Message);
		}
		catch (Exception ex)
		{
			return OperationResult.Fail("LinuxAssetSyncFailed", "Linux asset synchronization failed: " + ex.Message);
		}
		finally
		{
			foreach (StagedAsset asset in staged)
			{
				try
				{
					if (File.Exists(asset.StagedPath))
						File.Delete(asset.StagedPath);
				}
				catch
				{
				}
			}
		}
	}

	private static string GetRefreshFileForManifest(string manifestFile)
	{
		const string suffix = ".assets.json";
		return manifestFile.EndsWith(suffix, StringComparison.Ordinal)
			? manifestFile[..^suffix.Length] + ".refresh.json"
			: manifestFile + ".refresh.json";
	}

	private static async Task<OperationResult> ResolvePendingVinegarRefreshAsync(string refreshFile, string versionsDirectory, CancellationToken cancellationToken)
	{
		if (!File.Exists(refreshFile))
			return OperationResult.Success();

		JsonNode? node = await ReadJsonNodeAsync(refreshFile, cancellationToken).ConfigureAwait(false);
		if (node is not JsonObject state || state["deployments"] is not JsonArray deployments)
			return OperationResult.Fail("VinegarRefreshStateInvalid", "The Vinegar refresh state is invalid");
		string versionsRoot = Path.GetFullPath(versionsDirectory);
		foreach (JsonNode? entry in deployments)
		{
			if (entry is not JsonValue value || !value.TryGetValue(out string? name) || !IsSafeRelativePath(name))
				return OperationResult.Fail("VinegarRefreshStateInvalid", "The Vinegar refresh state contains an invalid deployment");
			OperationResult<string> pathResult = ResolveContainedPath(versionsRoot, name);
			if (!pathResult.Succeeded || pathResult.Value is null)
				return OperationResult.Fail("VinegarRefreshStateInvalid", "The Vinegar refresh state contains an unsafe deployment");
		}
		OperationResult<List<string>> currentDeployments = GetVinegarDeployments(versionsRoot);
		if (!currentDeployments.Succeeded || currentDeployments.Value is null)
			return OperationResult.Fail(currentDeployments.Failure!.Code, currentDeployments.Failure.Message, currentDeployments.Failure.State);
		if (currentDeployments.Value.Count > 0)
		{
			await WriteVinegarRefreshAsync(refreshFile, currentDeployments.Value, cancellationToken).ConfigureAwait(false);
			return OperationResult.Fail(
				"VinegarRefreshRequired",
				"Vinegar must refresh its installed Studio files before changed modifications can take effect. Open Vinegar Manage, choose Uninstall Studio, then retry the launch.",
				CapabilityState.RequiresExternalRuntime);
		}

		File.Delete(refreshFile);
		return OperationResult.Success();
	}

	private static OperationResult<List<string>> GetVinegarDeployments(string versionsDirectory)
	{
		string versionsRoot = Path.GetFullPath(versionsDirectory);
		if (!Directory.Exists(versionsRoot))
			return OperationResult<List<string>>.Success([]);
		if (IsSymbolicLink(new DirectoryInfo(versionsRoot)))
			return OperationResult<List<string>>.Fail("VinegarVersionsLinkRejected", "The Vinegar versions directory cannot be a symbolic link");

		List<string> deployments = [];
		foreach (DirectoryInfo directory in new DirectoryInfo(versionsRoot).EnumerateDirectories())
		{
			if (IsSymbolicLink(directory) || !IsSafeRelativePath(directory.Name))
				return OperationResult<List<string>>.Fail("VinegarDeploymentInvalid", "The Vinegar versions directory contains an unsafe deployment");
			deployments.Add(directory.Name);
		}
		deployments.Sort(StringComparer.Ordinal);
		return OperationResult<List<string>>.Success(deployments);
	}

	private static Task WriteVinegarRefreshAsync(string refreshFile, IEnumerable<string> deployments, CancellationToken cancellationToken)
	{
		JsonArray entries = new(deployments.Order(StringComparer.Ordinal).Select(static deployment => (JsonNode?)JsonValue.Create(deployment)).ToArray());
		JsonObject state = new()
		{
			["deployments"] = entries
		};
		return WriteJsonAtomicallyAsync(refreshFile, state, cancellationToken, true);
	}

	private static async Task<bool> FilesEqualAsync(string leftPath, string rightPath, CancellationToken cancellationToken)
	{
		FileInfo leftInfo = new(leftPath);
		FileInfo rightInfo = new(rightPath);
		if (leftInfo.Length != rightInfo.Length)
			return false;

		await using FileStream left = new(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await using FileStream right = new(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] leftHash = await SHA256.HashDataAsync(left, cancellationToken).ConfigureAwait(false);
		byte[] rightHash = await SHA256.HashDataAsync(right, cancellationToken).ConfigureAwait(false);
		return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
	}

	internal static OperationResult<List<SourceAsset>> MapToPackageAssets(List<SourceAsset> sourceAssets, SoberApkAssetIndex assetIndex)
	{
		List<SourceAsset> mapped = [];
		Dictionary<string, string> claimed = new(StringComparer.Ordinal);
		foreach (SourceAsset asset in sourceAssets)
		{
			OperationResult<string> resolved = assetIndex.Resolve(asset.RelativePath);
			if (!resolved.Succeeded || resolved.Value is null)
			{
				if (string.Equals(resolved.Failure?.Code, "SoberAssetNotInPackage", StringComparison.Ordinal))
					continue;
				return OperationResult<List<SourceAsset>>.Fail(resolved.Failure!.Code, resolved.Failure.Message, resolved.Failure.State);
			}

			if (claimed.TryGetValue(resolved.Value, out string? existing) && !string.Equals(existing, asset.RelativePath, StringComparison.Ordinal))
				return OperationResult<List<SourceAsset>>.Fail("SoberAssetCaseConflict", "Two modifications target the same Sober asset with different capitalization");

			claimed[resolved.Value] = asset.RelativePath;
			mapped.Add(new SourceAsset(resolved.Value, asset.SourcePath));
		}

		mapped.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
		return OperationResult<List<SourceAsset>>.Success(mapped);
	}

	private static OperationResult<List<SourceAsset>> EnumerateSourceAssets(string sourceRoot, Func<string, bool> exclude, CancellationToken cancellationToken)
	{
		List<SourceAsset> assets = [];
		if (!Directory.Exists(sourceRoot))
			return OperationResult<List<SourceAsset>>.Success(assets);

		Stack<string> pending = new();
		pending.Push(sourceRoot);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string directory = pending.Pop();
			foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (IsSymbolicLink(entry))
					return OperationResult<List<SourceAsset>>.Fail("LinuxAssetLinkRejected", "Symbolic links are not allowed in managed modifications");

				string relativePath = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, entry.FullName));
				if (!IsSafeRelativePath(relativePath))
					return OperationResult<List<SourceAsset>>.Fail("LinuxAssetPathInvalid", "A modification contains an unsafe relative path");

				if (entry is DirectoryInfo)
				{
					pending.Push(entry.FullName);
					continue;
				}

				if (entry is not FileInfo file || !IsRegularFile(file))
					return OperationResult<List<SourceAsset>>.Fail("LinuxAssetTypeRejected", "Managed modifications must contain regular files only");
				if (!exclude(relativePath))
					assets.Add(new SourceAsset(relativePath, entry.FullName));
			}
		}

		assets.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
		return OperationResult<List<SourceAsset>>.Success(assets);
	}

	private static async Task<OperationResult<HashSet<string>>> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
	{
		HashSet<string> files = new(StringComparer.Ordinal);
		if (!File.Exists(manifestPath))
			return OperationResult<HashSet<string>>.Success(files);

		JsonNode? node = await ReadJsonNodeAsync(manifestPath, cancellationToken).ConfigureAwait(false);
		if (node is not JsonObject manifest || manifest["files"] is not JsonArray entries)
			return OperationResult<HashSet<string>>.Fail("LinuxAssetManifestInvalid", "The managed asset manifest must contain a files array");

		foreach (JsonNode? entry in entries)
		{
			if (entry is not JsonValue value || !value.TryGetValue(out string? relativePath) || string.IsNullOrWhiteSpace(relativePath))
				return OperationResult<HashSet<string>>.Fail("LinuxAssetManifestInvalid", "The managed asset manifest contains an invalid path");
			if (relativePath.Contains('\\'))
				return OperationResult<HashSet<string>>.Fail("LinuxAssetPathInvalid", "The managed asset manifest contains an unsafe path");
			string normalized = NormalizeRelativePath(relativePath);
			if (!IsSafeRelativePath(normalized))
				return OperationResult<HashSet<string>>.Fail("LinuxAssetPathInvalid", "The managed asset manifest contains an unsafe path");
			if (!files.Add(normalized))
				return OperationResult<HashSet<string>>.Fail("LinuxAssetManifestInvalid", "The managed asset manifest contains duplicate paths");
		}

		return OperationResult<HashSet<string>>.Success(files);
	}

	private static Task WriteManifestAsync(string manifestPath, IEnumerable<string> files, CancellationToken cancellationToken)
	{
		JsonArray entries = new(files.Order(StringComparer.Ordinal).Select(static file => (JsonNode?)JsonValue.Create(file)).ToArray());
		JsonObject manifest = new()
		{
			["files"] = entries
		};
		return WriteJsonAtomicallyAsync(manifestPath, manifest, cancellationToken, true);
	}

	private static OperationResult<JsonNode?> ConvertFlagValue(string name, JsonNode? value)
	{
		if (value is null)
			return OperationResult<JsonNode?>.Success(null);
		if (value is not JsonValue jsonValue)
			return OperationResult<JsonNode?>.Fail("SoberFlagValueInvalid", "Client settings contain a flag value that Sober cannot use");

		if (!jsonValue.TryGetValue(out string? text) || text is null)
			return OperationResult<JsonNode?>.Success(value.DeepClone());

		if (IsBooleanFlag(name) && bool.TryParse(text, out bool booleanValue))
			return OperationResult<JsonNode?>.Success(JsonValue.Create(booleanValue));
		if (IsIntegerFlag(name) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integerValue))
			return OperationResult<JsonNode?>.Success(JsonValue.Create(integerValue));
		return OperationResult<JsonNode?>.Success(JsonValue.Create(text));
	}

	private static bool IsBooleanFlag(string name)
	{
		return name.StartsWith("FFlag", StringComparison.Ordinal)
			|| name.StartsWith("DFFlag", StringComparison.Ordinal)
			|| name.StartsWith("SFFlag", StringComparison.Ordinal);
	}

	private static bool IsIntegerFlag(string name)
	{
		return name.StartsWith("FInt", StringComparison.Ordinal)
			|| name.StartsWith("DFInt", StringComparison.Ordinal)
			|| name.StartsWith("SFInt", StringComparison.Ordinal);
	}

	private static string ExtractLeadingComments(string content)
	{
		StringBuilder header = new();
		foreach (string line in content.Split('\n'))
		{
			string trimmed = line.TrimEnd('\r');
			if (!trimmed.TrimStart().StartsWith("//", StringComparison.Ordinal))
				break;
			header.Append(trimmed).Append('\n');
		}

		return header.ToString();
	}

	private static JsonNode? ParseJsonNode(string content)
	{
		return JsonNode.Parse(content, nodeOptions: null, DocumentOptions);
	}

	private static async Task<JsonNode?> ReadJsonNodeAsync(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		return await JsonNode.ParseAsync(stream, nodeOptions: null, DocumentOptions, cancellationToken).ConfigureAwait(false);
	}

	private static async Task WriteJsonAtomicallyAsync(string path, JsonNode node, CancellationToken cancellationToken, bool privateFile)
	{
		string? directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory))
			throw new IOException("The JSON destination directory is invalid");
		Directory.CreateDirectory(directory);
		UnixFileMode? existingMode = !OperatingSystem.IsWindows() && File.Exists(path) ? File.GetUnixFileMode(path) : null;
		string temporary = Path.Combine(directory, ".voidstrap." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await JsonSerializer.SerializeAsync(stream, node, JsonOptions, cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(true);
			}
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(temporary, existingMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken, bool privateFile)
	{
		string? directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory))
			throw new IOException("The text destination directory is invalid");
		Directory.CreateDirectory(directory);
		UnixFileMode? existingMode = !OperatingSystem.IsWindows() && File.Exists(path) ? File.GetUnixFileMode(path) : null;
		string temporary = Path.Combine(directory, ".voidstrap." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			await using (StreamWriter writer = new(stream, new UTF8Encoding(false), 65536, true))
			{
				await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
				await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(true);
			}
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(temporary, existingMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
	{
		await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
		await output.FlushAsync(cancellationToken).ConfigureAwait(false);
		output.Flush(true);
	}

	private static OperationResult ValidateSourceRootSafety(string sourceRoot)
	{
		if (!Directory.Exists(sourceRoot))
			return OperationResult.Success();
		return IsSymbolicLink(new DirectoryInfo(sourceRoot))
			? OperationResult.Fail("LinuxAssetLinkRejected", "The modifications directory cannot be a symbolic link")
			: OperationResult.Success();
	}

	private static OperationResult ValidateExistingFileSafety(string root, string file)
	{
		OperationResult<string> contained = ResolveContainedPath(root, NormalizeRelativePath(Path.GetRelativePath(root, file)));
		if (!contained.Succeeded)
			return OperationResult.Fail(contained.Failure!.Code, contained.Failure.Message, contained.Failure.State);
		OperationResult parentSafety = ValidateExistingAncestors(root, Path.GetDirectoryName(file));
		if (!parentSafety.Succeeded)
			return parentSafety;
		FileInfo source = new(file);
		if (IsSymbolicLink(source))
			return OperationResult.Fail("LinuxAssetLinkRejected", "Symbolic links are not allowed in managed modifications");
		return IsRegularFile(source)
			? OperationResult.Success()
			: OperationResult.Fail("LinuxAssetTypeRejected", "Managed modifications must contain regular files only");
	}

	private static bool IsRegularFile(FileInfo file)
	{
		if (!OperatingSystem.IsLinux())
			return (file.Attributes & FileAttributes.Device) == 0;
		try
		{
			return GetLinuxFileStatus(
				LinuxCurrentWorkingDirectory,
				file.FullName,
				LinuxDoNotFollowLinks,
				LinuxFileTypeMaskRequest,
				out LinuxFileStatus status) == 0
				&& (status.Mode & LinuxFileTypeMask) == LinuxRegularFileType;
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
			return false;
		}
	}

	private static OperationResult EnsureDirectorySafety(string directory)
	{
		string fullPath = Path.GetFullPath(directory);
		if (Directory.Exists(fullPath) && IsSymbolicLink(new DirectoryInfo(fullPath)))
			return OperationResult.Fail("LinuxAssetLinkRejected", "A managed runtime directory cannot be a symbolic link");
		Directory.CreateDirectory(fullPath);
		return IsSymbolicLink(new DirectoryInfo(fullPath))
			? OperationResult.Fail("LinuxAssetLinkRejected", "A managed runtime directory cannot be a symbolic link")
			: OperationResult.Success();
	}

	private static OperationResult EnsureContainedDirectorySafety(string root, string directory)
	{
		string relative = NormalizeRelativePath(Path.GetRelativePath(root, directory));
		OperationResult<string> contained = ResolveContainedPath(root, relative);
		if (!contained.Succeeded)
			return OperationResult.Fail(contained.Failure!.Code, contained.Failure.Message, contained.Failure.State);
		OperationResult existingSafety = ValidateExistingAncestors(root, directory);
		if (!existingSafety.Succeeded)
			return existingSafety;
		Directory.CreateDirectory(directory);
		return ValidateExistingAncestors(root, directory);
	}

	private static OperationResult ValidateDestinationFileSafety(string root, string file)
	{
		string relative = NormalizeRelativePath(Path.GetRelativePath(root, file));
		OperationResult<string> contained = ResolveContainedPath(root, relative);
		if (!contained.Succeeded)
			return OperationResult.Fail(contained.Failure!.Code, contained.Failure.Message, contained.Failure.State);
		OperationResult ancestors = ValidateExistingAncestors(root, Path.GetDirectoryName(file));
		if (!ancestors.Succeeded)
			return ancestors;
		if (IsSymbolicLink(new FileInfo(file)))
			return OperationResult.Fail("LinuxAssetLinkRejected", "A managed runtime file cannot be a symbolic link");
		return OperationResult.Success();
	}

	private static OperationResult ValidateExistingAncestors(string root, string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory))
			return OperationResult.Fail("LinuxAssetPathInvalid", "A managed runtime path is invalid");
		string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		if (!string.Equals(current, rootFull, PathComparison) && !IsContainedPath(rootFull, current))
			return OperationResult.Fail("LinuxAssetPathInvalid", "A managed runtime path escapes its root directory");

		while (true)
		{
			if (Directory.Exists(current) && IsSymbolicLink(new DirectoryInfo(current)))
				return OperationResult.Fail("LinuxAssetLinkRejected", "A managed runtime path contains a symbolic link");
			if (string.Equals(current, rootFull, PathComparison))
				break;
			string? parent = Path.GetDirectoryName(current);
			if (string.IsNullOrWhiteSpace(parent))
				return OperationResult.Fail("LinuxAssetPathInvalid", "A managed runtime path escapes its root directory");
			current = Path.TrimEndingDirectorySeparator(parent);
		}
		return OperationResult.Success();
	}

	private static OperationResult<string> ResolveContainedPath(string root, string relativePath)
	{
		string normalized = NormalizeRelativePath(relativePath);
		if (!IsSafeRelativePath(normalized))
			return OperationResult<string>.Fail("LinuxAssetPathInvalid", "A managed asset path is unsafe");
		string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		string candidate = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
		if (!IsContainedPath(rootFull, candidate))
			return OperationResult<string>.Fail("LinuxAssetPathInvalid", "A managed asset path escapes its root directory");
		return OperationResult<string>.Success(candidate);
	}

	private static bool IsContainedPath(string root, string candidate)
	{
		string rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
		string candidateFull = Path.GetFullPath(candidate);
		return candidateFull.StartsWith(rootPrefix, PathComparison);
	}

	private static string NormalizeRelativePath(string path)
	{
		return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
	}

	private static bool IsSafeRelativePath(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal) || relativePath.Contains('\\'))
			return false;
		string[] segments = relativePath.Split('/', StringSplitOptions.None);
		return segments.All(static segment => segment.Length > 0 && segment != "." && segment != "..");
	}

	private static bool IsSymbolicLink(FileSystemInfo entry)
	{
		entry.Refresh();
		return entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);
	}

	private static void RemoveEmptyParents(string root, string? directory)
	{
		string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		while (!string.IsNullOrWhiteSpace(directory))
		{
			string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
			if (string.Equals(current, rootFull, PathComparison) || !IsContainedPath(rootFull, current))
				return;
			if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
				return;
			Directory.Delete(current);
			directory = Path.GetDirectoryName(current);
		}
	}

	private sealed record SoberOwnershipManifest(HashSet<string> Flags, HashSet<string> Settings);

	internal sealed record SourceAsset(string RelativePath, string SourcePath);

	private sealed record StagedAsset(string StagedPath, string DestinationPath);
}
