using System.Collections.Generic;

namespace Voidstrap.Models.Persistable;

public class AppState
{
	public string VersionGuid { get; set; } = string.Empty;

	public Dictionary<string, string> PackageHashes { get; set; } = new Dictionary<string, string>();

	public int Size { get; set; }

	public List<string> ModManifest { get; set; } = new List<string>();

	public Dictionary<string, string> ModApplyCache { get; set; } = new Dictionary<string, string>();

	public Dictionary<string, List<string>> ManagedModManifest { get; set; } = new Dictionary<string, List<string>>();

	public string ModApplyVersion { get; set; } = string.Empty;

	public List<string>? CriticalFiles { get; set; }
}
