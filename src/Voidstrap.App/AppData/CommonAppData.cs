using System.Collections.Generic;
using System;
using System.IO;
using Voidstrap.Models.Persistable;

namespace Voidstrap.AppData;

public abstract class CommonAppData
{
	private IReadOnlyDictionary<string, string> _commonMap { get; } = new Dictionary<string, string>
	{
		{ "Libraries.zip", "" },
		{ "redist.zip", "" },
		{ "shaders.zip", "shaders\\" },
		{ "ssl.zip", "ssl\\" },
		{ "WebView2.zip", "" },
		{ "WebView2RuntimeInstaller.zip", "WebView2RuntimeInstaller\\" },
		{ "content-avatar.zip", "content\\avatar\\" },
		{ "content-configs.zip", "content\\configs\\" },
		{ "content-fonts.zip", "content\\fonts\\" },
		{ "content-sky.zip", "content\\sky\\" },
		{ "content-sounds.zip", "content\\sounds\\" },
		{ "content-textures2.zip", "content\\textures\\" },
		{ "content-models.zip", "content\\models\\" },
		{ "content-textures3.zip", "PlatformContent\\pc\\textures\\" },
		{ "content-terrain.zip", "PlatformContent\\pc\\terrain\\" },
		{ "content-platform-fonts.zip", "PlatformContent\\pc\\fonts\\" },
		{ "content-platform-dictionaries.zip", "PlatformContent\\pc\\shared_compression_dictionaries\\" },
		{ "extracontent-luapackages.zip", "ExtraContent\\LuaPackages\\" },
		{ "extracontent-translations.zip", "ExtraContent\\translations\\" },
		{ "extracontent-models.zip", "ExtraContent\\models\\" },
		{ "extracontent-textures.zip", "ExtraContent\\textures\\" },
		{ "extracontent-places.zip", "ExtraContent\\places\\" }
	};

	public virtual string ExecutableName { get; }

	public abstract string BinaryType { get; }

	public virtual string VersionsRoot => Paths.Versions;

	public string InstallFolderName
	{
		get
		{
			if (App.Settings.Prop.StaticDirectory && !string.IsNullOrEmpty(BinaryType))
			{
				return BinaryType;
			}
			return IsVersionGuidValid(State.VersionGuid) ? State.VersionGuid : string.Empty;
		}
	}

	public string Directory
	{
		get
		{
			string root = Path.GetFullPath(VersionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string folder = InstallFolderName;
			if (string.IsNullOrEmpty(folder))
			{
				return root;
			}
			string candidate = Path.GetFullPath(Path.Combine(root, folder));
			string prefix = root + Path.DirectorySeparatorChar;
			return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : root;
		}
	}

	public bool TryMigrateInstallDirectory(bool toStatic)
	{
		try
		{
			if (!IsVersionGuidValid(State.VersionGuid) || string.IsNullOrEmpty(BinaryType))
			{
				return false;
			}
			string root = Path.GetFullPath(VersionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string versionPath = Path.Combine(root, State.VersionGuid);
			string staticPath = Path.Combine(root, BinaryType);
			string source = toStatic ? versionPath : staticPath;
			string destination = toStatic ? staticPath : versionPath;
			if (!System.IO.Directory.Exists(source) || System.IO.Directory.Exists(destination))
			{
				return false;
			}
			if (!File.Exists(Path.Combine(source, ExecutableName)))
			{
				return false;
			}
			System.IO.Directory.Move(source, destination);
			App.Logger?.WriteLine("CommonAppData::TryMigrateInstallDirectory", "Moved " + BinaryType + " from " + Path.GetFileName(source) + " to " + Path.GetFileName(destination));
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("CommonAppData::TryMigrateInstallDirectory", "Could not move the existing install, it will be downloaded again: " + ex.Message);
			return false;
		}
	}

	public string ExecutablePath => Path.Combine(Directory, ExecutableName);

	public virtual AppState State { get; }

	public virtual IReadOnlyDictionary<string, string> PackageDirectoryMap { get; set; }

	public virtual IReadOnlyList<string> CandidateCriticalFiles => [];

	public static bool IsVersionGuidValid(string? versionGuid)
	{
		if (versionGuid is not { Length: 24 } || !versionGuid.StartsWith("version-", StringComparison.Ordinal))
		{
			return false;
		}
		foreach (char character in versionGuid.AsSpan(8))
		{
			if (!Uri.IsHexDigit(character))
			{
				return false;
			}
		}
		return true;
	}

	public CommonAppData()
	{
		if (PackageDirectoryMap == null)
		{
			PackageDirectoryMap = _commonMap;
			return;
		}
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		foreach (KeyValuePair<string, string> item in _commonMap)
		{
			dictionary[item.Key] = item.Value;
		}
		foreach (KeyValuePair<string, string> item2 in PackageDirectoryMap)
		{
			dictionary[item2.Key] = item2.Value;
		}
		PackageDirectoryMap = dictionary;
	}
}
