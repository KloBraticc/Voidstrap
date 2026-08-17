using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.Config;

public class SupporterGroup
{
	[JsonPropertyName("columns")]
	public int Columns { get; set; }

	[JsonPropertyName("supporters")]
	public List<Supporter> Supporters { get; set; } = Enumerable.Empty<Supporter>().ToList();
}
