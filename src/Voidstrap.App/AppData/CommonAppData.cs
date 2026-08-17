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

	public virtual string VersionsRoot => Paths.Versions;

	public string Directory
	{
		get
		{
			string root = Path.GetFullPath(VersionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (!IsVersionGuidValid(State.VersionGuid))
			{
				return root;
			}
			string candidate = Path.GetFullPath(Path.Combine(root, State.VersionGuid));
			string prefix = root + Path.DirectorySeparatorChar;
			return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : root;
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
