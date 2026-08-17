using System;
using System.Collections.Generic;

namespace Voidstrap.Models.Persistable;

public class MatchmakerAttempt
{
	public int Count { get; set; }

	public DateTime LastUtc { get; set; }

	public List<string> TriedJobIds { get; set; } = new List<string>();
}
