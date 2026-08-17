using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Voidstrap.Integrations;

public sealed class LearnedServerEntry
{
	public string Cidr { get; set; } = "";

	public string City { get; set; } = "";

	public string Region { get; set; } = "";

	public string Country { get; set; } = "";

	public double Lat { get; set; }

	public double Lon { get; set; }

	public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

	public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

	public int SeenCount { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<string>? IPs { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<int>? PingSamplesMs { get; set; }

	[JsonIgnore]
	public double AveragePingMs
	{
		get
		{
			if (PingSamplesMs != null && PingSamplesMs.Count != 0)
			{
				return PingSamplesMs.Average();
			}
			return -1.0;
		}
	}

	[JsonIgnore]
	public int BestPingMs
	{
		get
		{
			if (PingSamplesMs != null && PingSamplesMs.Count != 0)
			{
				return PingSamplesMs.Min();
			}
			return -1;
		}
	}
}
