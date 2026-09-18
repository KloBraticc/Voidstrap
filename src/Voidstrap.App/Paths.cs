using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;

namespace Voidstrap;

internal static class Paths
{
	private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	public static string Temp => Path.Combine(Path.GetTempPath(), "Voidstrap");

	public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

	public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

	public static string WindowsStartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");

	public static string System => Environment.GetFolderPath(Environment.SpecialFolder.System);

	public static string Process => Environment.ProcessPath!;

	public static string LaunchExecutable => Voidstrap.Utility.Platform.IsLinux ? LinuxAppImageHost.ResolveApplicationPath(Process) : Process;

	public static string TempUpdates => Path.Combine(Temp, "Updates");

	public static string TempLogs => Path.Combine(Temp, "Logs");

	private static string? _userData;

	public static string UserData => _userData ??= ResolveUserData();

	public static string DocumentsData
	{
		get
		{
			string documents = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
			return string.IsNullOrWhiteSpace(documents) ? Path.Combine(UserData, "Account") : Path.Combine(documents, "Voidstrap");
		}
	}

	private static string ResolveUserData()
	{
		string preferred = Path.Combine(LocalAppData, "Voidstrap");
		try
		{
			Directory.CreateDirectory(preferred);
			MigrateLegacyUserData(preferred);
			return preferred;
		}
		catch
		{
		}
		try
		{
			string fallback = Path.Combine(Temp, "UserData");
			Directory.CreateDirectory(fallback);
			return fallback;
		}
		catch
		{
		}
		return preferred;
	}

	private static void MigrateLegacyUserData(string target)
	{
		try
		{
			string marker = Path.Combine(target, ".legacy-documents-migrated-v1");
			if (File.Exists(marker))
				return;
			string documents = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
			if (string.IsNullOrWhiteSpace(documents))
				return;
			string legacy = Path.Combine(documents, "Voidstrap");
			if (string.Equals(Path.TrimEndingDirectorySeparator(legacy), Path.TrimEndingDirectorySeparator(target), PathComparison))
				return;
			if (!Directory.Exists(legacy))
				return;
			if (CopyMissing(legacy, target))
				WriteMigrationMarker(marker);
		}
		catch
		{
		}
	}

	private static bool CopyMissing(string source, string destination)
	{
		bool succeeded = true;
		Stack<(string Source, string Destination)> pending = new();
		pending.Push((source, destination));
		while (pending.Count > 0)
		{
			(string currentSource, string currentDestination) = pending.Pop();
			try
			{
				Directory.CreateDirectory(currentDestination);
				foreach (string file in Directory.EnumerateFiles(currentSource))
				{
					try
					{
						string copied = Path.Combine(currentDestination, Path.GetFileName(file));
						if (!File.Exists(copied))
							File.Copy(file, copied);
					}
					catch
					{
						succeeded = false;
					}
				}
				foreach (string directory in Directory.EnumerateDirectories(currentSource))
				{
					try
					{
						DirectoryInfo info = new(directory);
						if (info.LinkTarget == null)
							pending.Push((directory, Path.Combine(currentDestination, info.Name)));
					}
					catch
					{
						succeeded = false;
					}
				}
			}
			catch
			{
				succeeded = false;
			}
		}
		return succeeded;
	}

	private static void WriteMigrationMarker(string marker, string content = "1")
	{
		string temporary = marker + ".tmp";
		File.WriteAllText(temporary, content);
		File.Move(temporary, marker, overwrite: true);
	}

	public static bool TryEnsureDirectory(string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory))
			return false;
		try
		{
			Directory.CreateDirectory(directory);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static string Base { get; private set; } = "";

	public static string Application { get; private set; } = "";

	public static string RobloxBase { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voidstrap");

	public static string Config { get; private set; } = "";

	public static string Data { get; private set; } = "";

	public static string Cache { get; private set; } = "";

	public static string Backups { get; private set; } = "";

	public static string Themes { get; private set; } = "";

	public static string Media { get; private set; } = "";

	public static string Extensions { get; private set; } = "";

	public static string Logs { get; private set; } = "";

	public static string Downloads { get; private set; } = "";

	public static string Integrations { get; private set; } = "";

	public static string Roblox { get; private set; } = "";

	public static string Versions { get; private set; } = "";

	public static string Mods { get; private set; } = "";

	public static string RobloxClients { get; private set; } = "";

	public static string WebViewData { get; private set; } = "";

	public static string RobloxLogs { get; private set; } = "";

	public static string RobloxCache { get; private set; } = "";

	public static string SavedBackups { get; private set; } = "";

	public static string AccountBackups { get; private set; } = "";

	public static string CustomThemes { get; private set; } = "";

	public static string CustomCursors { get; private set; } = "";

	public static string CustomThemeXaml { get; private set; } = "";

	public static string EditorTheme { get; private set; } = "";

	public static string RiShade { get; private set; } = "";

	public static string Fleasion { get; private set; } = "";

	public static string ApiDumpTool { get; private set; } = "";

	public static string Rojo { get; private set; } = "";

	public static string ServerFetch { get; private set; } = "";

	public static string AssetProxy { get; private set; } = "";

	public static string AssetExport { get; private set; } = "";

	public static string AssetCache { get; private set; } = "";

	public static string ClientImageCache { get; private set; } = "";

	public static string SkyboxPack { get; private set; } = "";

	public static string CustomSkybox { get; private set; } = "";

	public static string NipProfiles { get; private set; } = "";

	public static string Mobile { get; private set; } = "";

	public static string Library { get; private set; } = "";

	public static string ServerHistory { get; private set; } = "";

	public static string PlayTimeStore { get; private set; } = "";

	public static string BackgroundSettings { get; private set; } = "";

	public static string CustomFont => Path.Combine(Mods, "content", "fonts", "CustomFont.ttf");

	public static string CustomFontSource => Path.Combine(Data, "CustomFontSource.bin");

	public static string CustomDeathSound => Path.Combine(Mods, "Content", "sounds", "oof.ogg");

	public static string CustomDeathSoundSource => Path.Combine(Data, "CustomDeathSoundSource.ogg");

	public static string ManagedMods => Path.Combine(Data, "ManagedMods");

	public static string ManagedModPackages => Path.Combine(ManagedMods, "Packages");

	public static string ManagedModIndex => Path.Combine(ManagedMods, "Index.json");

	public static bool Initialized => !string.IsNullOrWhiteSpace(Base);

	public static bool CloudSynced { get; private set; }

	public static bool LegacyLayoutReset { get; private set; }

	public static void Initialize(string baseDirectory, string? robloxBaseDirectory = null)
	{
		if (string.IsNullOrWhiteSpace(baseDirectory))
		{
			throw new ArgumentException("Base directory cannot be null or empty.", nameof(baseDirectory));
		}
		Base = baseDirectory;
		CloudSynced = Voidstrap.Installer.IsCloudSyncedPath(baseDirectory);
		string offloadRoot = (robloxBaseDirectory ?? (CloudSynced ? Path.Combine(LocalAppData, "Voidstrap") : baseDirectory));
		RobloxBase = offloadRoot;
		Application = Path.Combine(Base, Voidstrap.Utility.Platform.IsWindows ? "Voidstrap.exe" : "Voidstrap");
		Config = Path.Combine(Base, "Config");
		Data = Path.Combine(Base, "Data");
		Cache = Path.Combine(offloadRoot, "Cache");
		Backups = Path.Combine(Base, "Backups");
		Themes = Path.Combine(Base, "Themes");
		Media = Path.Combine(Base, "Media");
		Extensions = Path.Combine(Base, "Extensions");
		Logs = Path.Combine(offloadRoot, "Logs");
		Downloads = Path.Combine(offloadRoot, "Downloads");
		Integrations = Path.Combine(Base, "Integrations");
		Roblox = Path.Combine(Base, "Roblox");
		Versions = Path.Combine(RobloxBase, "RblxVersions");
		Mods = Path.Combine(Base, "VoidstrapMods");
		RobloxClients = Path.Combine(offloadRoot, "RobloxClients");
		WebViewData = Path.Combine(LocalAppData, "Voidstrap", "WebView2");
		RobloxLogs = Path.Combine(LocalAppData, "Roblox", "logs");
		InitializeDerivedPaths();
	}

	public static void InitializePortable(Voidstrap.Platform.PlatformStoragePaths storage, string applicationPath)
	{
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrWhiteSpace(applicationPath))
		{
			throw new ArgumentException("Application path cannot be null or empty.", nameof(applicationPath));
		}
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			applicationPath = LinuxAppImageHost.ResolveApplicationPath(applicationPath);
		}

		Base = storage.ApplicationSupport;
		CloudSynced = false;
		RobloxBase = storage.Data;
		Application = Path.GetFullPath(applicationPath);
		Config = storage.Configuration;
		Data = storage.Data;
		Cache = storage.Cache;
		Backups = Path.Combine(Data, "Backups");
		Themes = Path.Combine(Data, "Themes");
		Media = Path.Combine(Data, "Media");
		Extensions = storage.Extensions;
		Logs = storage.Logs;
		Downloads = Voidstrap.Utility.Platform.IsLinux ? Path.Combine(storage.Cache, "Downloads") : storage.Downloads;
		Integrations = Path.Combine(Data, "Integrations");
		Roblox = Path.Combine(Data, "Roblox");
		Versions = Path.Combine(RobloxBase, "RblxVersions");
		Mods = Path.Combine(Data, "VoidstrapMods");
		RobloxClients = Path.Combine(Data, "RobloxClients");
		WebViewData = Path.Combine(Cache, "WebView");
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		RobloxLogs = Voidstrap.Utility.Platform.IsLinux
			? Path.Combine(home, ".var", "app", "org.vinegarhq.Sober", "data", "sober", "appData", "logs")
			: OperatingSystem.IsMacOS()
				? Path.Combine(home, "Library", "Logs", "Roblox")
				: Path.Combine(LocalAppData, "Roblox", "logs");
		InitializeDerivedPaths(!Voidstrap.Utility.Platform.IsLinux);
	}

	private static void InitializeDerivedPaths(bool resetLegacyLayout = true)
	{
		SavedBackups = Path.Combine(Backups, "FlagProfiles");
		AccountBackups = Path.Combine(Backups, "Accounts");
		CustomThemes = Path.Combine(Themes, "Custom");
		CustomCursors = Path.Combine(Themes, "Cursors");
		CustomThemeXaml = Path.Combine(Themes, "Custom.xaml");
		EditorTheme = Path.Combine(Themes, "Editor-Theme-Custom.xshd");
		RiShade = Path.Combine(Extensions, "RiShade");
		Fleasion = Path.Combine(Extensions, "Fleasion");
		ApiDumpTool = Path.Combine(Extensions, "RobloxAPIDumpTool");
		Rojo = Path.Combine(Extensions, "Rojo");
		ServerFetch = Path.Combine(Data, "ServerFetch");
		AssetProxy = Path.Combine(Data, "AssetProxy");
		AssetExport = Path.Combine(Data, "AssetExport");
		SkyboxPack = Path.Combine(Data, "SkyboxPack");
		CustomSkybox = Path.Combine(Data, "CustomSkybox");
		NipProfiles = Path.Combine(Data, "NipProfiles");
		Mobile = Path.Combine(Data, "Mobile");
		AssetCache = Path.Combine(Cache, "Assets");
		ClientImageCache = Path.Combine(Cache, "ClientImages");
		Library = ResolveLibraryRoot();
		ServerHistory = Path.Combine(Library, "ServerHistory.json");
		PlayTimeStore = Path.Combine(Library, "PlayTimeStore.json");
		BackgroundSettings = Path.Combine(Config, "BackgroundSettings.json");
		MigrateLibrary();
		try
		{
			LegacyLayoutReset = resetLegacyLayout && IsLegacyInstall();
			if (LegacyLayoutReset)
			{
				ResetLegacyInstall();
			}
			Directory.CreateDirectory(Config);
			WriteVersionStamp();
		}
		catch (Exception ex)
		{
			LegacyLayoutReset = false;
			App.Logger?.WriteLine("Paths::InitializeDerivedPaths", "Legacy install check failed: " + ex.Message);
		}
	}

	private static string ResolveLibraryRoot()
	{
		string fallback = Path.Combine(Data, "Library");
		try
		{
			string documents = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
			if (string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents))
			{
				return fallback;
			}

			string root = Path.Combine(DocumentsData, "Library");
			Directory.CreateDirectory(root);
			return root;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("Paths::ResolveLibraryRoot", "The documents folder is not usable, keeping the library beside the app data: " + ex.Message);
			return fallback;
		}
	}

	private static void MigrateLibrary()
	{
		foreach ((string source, string destination) in new[]
		{
			(Path.Combine(Data, "ServerHistory.json"), ServerHistory),
			(Path.Combine(Data, "PlayTimeStore.json"), PlayTimeStore),
			(Path.Combine(Cache, "LibrarySnapshot.json"), Path.Combine(Library, "LibrarySnapshot.json"))
		})
		{
			try
			{
				if (string.Equals(source, destination, PathComparison) || !File.Exists(source) || File.Exists(destination))
				{
					continue;
				}

				Directory.CreateDirectory(Library);
				File.Move(source, destination);
				App.Logger?.WriteLine("Paths::MigrateLibrary", "Moved " + Path.GetFileName(source) + " into the library folder");
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("Paths::MigrateLibrary", "Could not move " + Path.GetFileName(source) + ": " + ex.Message);
			}
		}
	}

	public static void EnsureDirectories()
	{
		if (!Initialized)
		{
			return;
		}
		EnsureDirectoryExists(Config);
		EnsureDirectoryExists(Data);
		EnsureDirectoryExists(Library);
		EnsureDirectoryExists(Cache);
		EnsureDirectoryExists(Backups);
		EnsureDirectoryExists(Themes);
		EnsureDirectoryExists(Media);
		EnsureDirectoryExists(Extensions);
		EnsureDirectoryExists(Logs);
		EnsureDirectoryExists(Downloads);
		EnsureDirectoryExists(Integrations);
		EnsureDirectoryExists(Versions);
		EnsureDirectoryExists(Mods);
		EnsureDirectoryExists(SavedBackups);
		EnsureDirectoryExists(CustomThemes);
		EnsureDirectoryExists(RiShade);
	}
	private const string DataFormatVersion = "1.1.0.6";

	private static string VersionStampPath => Path.Combine(Config, ".version");

	private static string CurrentVersion => typeof(Paths).Assembly.GetName().Version?.ToString() ?? DataFormatVersion;

	private static bool IsBelowDataFormat(string? candidate)
	{
		return Version.TryParse(candidate, out Version? parsed) && Version.TryParse(DataFormatVersion, out Version? required) && parsed < required;
	}

	private static bool IsLegacyInstall()
	{
		if (!Directory.Exists(Base))
		{
			return false;
		}

		string? stamp = ReadVersionStamp();
		if (stamp != null)
		{
			return IsBelowDataFormat(stamp);
		}

		if (File.Exists(Path.Combine(Config, ".legacy-layout-reset-v1")))
		{
			return false;
		}

		if (IsBelowDataFormat(ReadApplicationVersion()))
		{
			return true;
		}

		return HasInstalledContent();
	}

	private static string? ReadVersionStamp()
	{
		try
		{
			return File.Exists(VersionStampPath) ? File.ReadAllText(VersionStampPath).Trim() : null;
		}
		catch
		{
			return null;
		}
	}

	private static string? ReadApplicationVersion()
	{
		try
		{
			return File.Exists(Application) ? FileVersionInfo.GetVersionInfo(Application).FileVersion : null;
		}
		catch
		{
			return null;
		}
	}

	private static bool HasInstalledContent()
	{
		try
		{
			string applicationName = Path.GetFileName(Application);
			foreach (string entry in Directory.EnumerateFileSystemEntries(Base))
			{
				string name = Path.GetFileName(entry);
				if (name.Length > 0 && !string.Equals(name, applicationName, PathComparison))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static void WriteVersionStamp()
	{
		try
		{
			string current = CurrentVersion;
			string? stored = ReadVersionStamp();
			if (string.Equals(stored, current, StringComparison.Ordinal))
			{
				return;
			}
			if (stored != null && Version.TryParse(stored, out Version? previous) && Version.TryParse(current, out Version? running) && previous > running)
			{
				return;
			}
			WriteMigrationMarker(VersionStampPath, current);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("Paths::WriteVersionStamp", "Could not write the install version stamp: " + ex.Message);
		}
	}

	private static void ResetLegacyInstall()
	{
		string root;
		try
		{
			root = Path.GetFullPath(Base).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch
		{
			return;
		}

		string? rootDrive = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (root.Length <= 3 || string.Equals(root, rootDrive, PathComparison) || string.Equals(root, UserProfile.TrimEnd(Path.DirectorySeparatorChar), PathComparison))
		{
			App.Logger?.WriteLine("Paths::ResetLegacyInstall", "Refusing to reset an unsafe install location: " + root);
			return;
		}

		if (!Directory.Exists(root))
		{
			return;
		}

		App.Logger?.WriteLine("Paths::ResetLegacyInstall", "Legacy install detected, clearing it for a fresh install: " + root);

		string keep = "";
		try
		{
			keep = Path.GetFullPath(Application);
		}
		catch
		{
		}

		string running = "";
		try
		{
			running = string.IsNullOrWhiteSpace(Process) ? "" : Path.GetFullPath(Process);
		}
		catch
		{
		}

		int removed = ClearDirectoryContents(root, keep, running);
		App.Logger?.WriteLine("Paths::ResetLegacyInstall", $"Removed {removed} entries from {root}");
		ResetLegacyUserData(root);
	}

	private static int ClearDirectoryContents(string root, string keep, string running)
	{
		int removed = 0;
		foreach (string entry in SafeEntries(root))
		{
			string full;
			try
			{
				full = Path.GetFullPath(entry);
			}
			catch
			{
				continue;
			}

			if ((keep.Length > 0 && string.Equals(full, keep, PathComparison)) || (running.Length > 0 && string.Equals(full, running, PathComparison)))
			{
				continue;
			}

			if (TryRemove(full))
			{
				removed++;
			}
		}
		return removed;
	}

	private static void ResetLegacyUserData(string root)
	{
		string userData;
		try
		{
			userData = Path.GetFullPath(Path.Combine(LocalAppData, "Voidstrap")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch
		{
			return;
		}

		if (!string.Equals(userData, root, PathComparison) && Directory.Exists(userData))
		{
			int removed = ClearDirectoryContents(userData, "", "");
			App.Logger?.WriteLine("Paths::ResetLegacyInstall", $"Removed {removed} entries from {userData}");
		}

		RemoveLegacyDocumentsData();

		try
		{
			Directory.CreateDirectory(userData);
			WriteMigrationMarker(Path.Combine(userData, ".legacy-documents-migrated-v1"));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("Paths::ResetLegacyInstall", "Could not seal the documents migration marker: " + ex.Message);
		}
	}

	private static void RemoveLegacyDocumentsData()
	{
		try
		{
			string documents = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
			if (string.IsNullOrWhiteSpace(documents))
			{
				return;
			}

			string documentsRoot = Path.GetFullPath(documents).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string legacy = Path.GetFullPath(Path.Combine(documentsRoot, "Voidstrap")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

			if (!string.Equals(Path.GetDirectoryName(legacy), documentsRoot, PathComparison) || !Directory.Exists(legacy))
			{
				return;
			}

			if (new DirectoryInfo(legacy).LinkTarget != null)
			{
				App.Logger?.WriteLine("Paths::ResetLegacyInstall", "Leaving the linked legacy documents folder alone: " + legacy);
				return;
			}

			int removed = 0;
			bool keptLibrary = false;
			foreach (string entry in SafeEntries(legacy))
			{
				if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(entry)), "Library", StringComparison.OrdinalIgnoreCase))
				{
					keptLibrary = true;
					continue;
				}

				if (TryRemove(entry))
				{
					removed++;
				}
			}

			if (!keptLibrary)
			{
				TryRemove(legacy);
			}

			App.Logger?.WriteLine("Paths::ResetLegacyInstall", $"Cleared the legacy documents data at {legacy} ({removed} removed)");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("Paths::ResetLegacyInstall", "Could not remove the legacy documents data: " + ex.Message);
		}
	}

	private static string[] SafeEntries(string root)
	{
		try
		{
			return Directory.GetFileSystemEntries(root);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("Paths::ResetLegacyInstall", "Could not list " + root + ": " + ex.Message);
			return [];
		}
	}

	private static bool TryRemove(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				DirectoryInfo info = new(path);
				if (info.LinkTarget != null)
				{
					info.Delete();
					return true;
				}
				ClearReadOnly(path);
				Directory.Delete(path, recursive: true);
				return true;
			}
			if (File.Exists(path))
			{
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
				return true;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("Paths::ResetLegacyInstall", "Could not remove " + path + ": " + ex.Message);
		}
		return false;
	}

	public static void ResetUserData()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			ResetLinuxUserData();
			return;
		}

		string basePath = Path.GetFullPath(Base).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string? baseRoot = Path.GetPathRoot(basePath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (string.IsNullOrWhiteSpace(basePath) || string.Equals(basePath, baseRoot, PathComparison))
		{
			throw new InvalidOperationException("The Voidstrap data location is unsafe to reset");
		}

		string[] directories =
		[
			Config,
			Data,
			Cache,
			Backups,
			Themes,
			Media,
			Extensions,
			Logs,
			Downloads,
			Integrations,
			Roblox,
			Versions,
			Mods,
			RobloxClients,
			WebViewData,
			Temp
		];

		foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length))
		{
			DeleteOwnedDirectory(directory, basePath);
		}

		string[] files =
		[
			"State.json",
			"AppSettings.json",
			"DownloadStats.json",
			"RobloxState.json",
			"ServerHistory.json",
			"PlayTimeStore.json",
			"BackgroundSettings.json",
			"ModManifest.txt"
		];

		foreach (string fileName in files)
		{
			string path = Path.Combine(basePath, fileName);
			if (File.Exists(path))
			{
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
			}
		}

		Directory.CreateDirectory(Config);
		WriteVersionStamp();
	}

	private static void ResetLinuxUserData()
	{
		IPlatformHost? host = Voidstrap.Utility.Platform.RuntimeHost;
		if (host == null)
		{
			throw new InvalidOperationException("Linux platform storage is unavailable");
		}

		PlatformStoragePaths storage = host.Paths.Storage;
		string logsRoot = Path.GetDirectoryName(Path.GetFullPath(storage.Logs)) ?? storage.Logs;
		List<string> directories =
		[
			storage.Configuration,
			storage.ApplicationSupport,
			storage.Data,
			storage.Cache,
			logsRoot,
			UserData,
			Temp
		];
		if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(DocumentsData)), "voidstrap", StringComparison.OrdinalIgnoreCase))
		{
			directories.Add(DocumentsData);
		}

		foreach (string directory in directories.Distinct(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).OrderByDescending(path => path.Length))
		{
			DeleteLinuxOwnedDirectory(directory);
		}
	}

	private static void DeleteLinuxOwnedDirectory(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return;
		}

		string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string? pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string profileRoot = Path.GetFullPath(UserProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string name = Path.GetFileName(fullPath);
		if (string.IsNullOrWhiteSpace(fullPath) || string.Equals(fullPath, pathRoot, PathComparison) || string.Equals(fullPath, profileRoot, PathComparison) || !string.Equals(name, "voidstrap", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("The Linux Voidstrap data location is unsafe to reset");
		}

		string running = Path.GetFullPath(Process);
		if (running.StartsWith(fullPath + Path.DirectorySeparatorChar, PathComparison))
		{
			App.Logger?.WriteLine("Paths::ResetLinuxUserData", "Preserving the folder that contains the running executable: " + fullPath);
			return;
		}

		DirectoryInfo info = new(fullPath);
		if (info.LinkTarget != null)
		{
			info.Delete();
			return;
		}

		ClearReadOnly(fullPath);
		Directory.Delete(fullPath, recursive: true);
	}

	private static void DeleteOwnedDirectory(string path, string basePath)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return;
		}

		string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string webViewPath = Path.GetFullPath(WebViewData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string tempPath = Path.GetFullPath(Temp).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string offloadPath = Path.GetFullPath(RobloxBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		bool insideBase = fullPath.StartsWith(basePath + Path.DirectorySeparatorChar, PathComparison);
		bool insideOffload = fullPath.StartsWith(offloadPath + Path.DirectorySeparatorChar, PathComparison);
		if (!insideBase && !insideOffload && !string.Equals(fullPath, webViewPath, PathComparison) && !string.Equals(fullPath, tempPath, PathComparison))
		{
			throw new InvalidOperationException("A Voidstrap data location is outside the expected folders");
		}

		Exception? failure = null;
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				ClearReadOnly(fullPath);
				Directory.Delete(fullPath, recursive: true);
				return;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				failure = ex;
				if (attempt < 2)
				{
					Task.Delay(500).GetAwaiter().GetResult();
				}
			}
		}

		throw new IOException("Voidstrap data could not be removed from " + fullPath, failure);
	}

	private static void ClearReadOnly(string path)
	{
		EnumerationOptions options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint
		};
		foreach (string file in Directory.EnumerateFiles(path, "*", options))
		{
			try
			{
				FileAttributes attributes = File.GetAttributes(file);
				if ((attributes & FileAttributes.ReadOnly) != 0)
				{
					File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
				}
			}
			catch
			{
			}
		}
	}

	private static void EnsureDirectoryExists(string directoryPath)
	{
		if (!Directory.Exists(directoryPath))
		{
			try
			{
				Directory.CreateDirectory(directoryPath);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("Paths::EnsureDirectoryExists", "Failed to create directory " + directoryPath + ": " + ex.Message);
			}
		}
	}
}
