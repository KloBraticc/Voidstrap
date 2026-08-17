using System;

namespace Voidstrap.Integrations.Studio;

public sealed class StudioState
{
	public bool Sharing { get; set; } = true;

	public string Place { get; set; } = "";

	public long PlaceId { get; set; }

	public long UniverseId { get; set; }

	public string Creator { get; set; } = "";

	public string Script { get; set; } = "";

	public int ScriptLines { get; set; }

	public string Mode { get; set; } = "";

	public int Selection { get; set; }

	public string SelectionClass { get; set; } = "";

	public string Custom { get; set; } = "";

	public DateTime ReceivedUtc { get; set; }
}
