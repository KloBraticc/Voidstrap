using System.Collections.Generic;

namespace Voidstrap.Models.Persistable;

public class RobloxState
{
	public AppState Player { get; set; } = new AppState();

	public AppState Studio { get; set; } = new AppState();

	public List<string> ModManifest { get; set; } = new List<string>();
}
