using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Voidstrap.Models;

public class FontFamily
{
	[JsonPropertyName("name")]
	public string Name { get; set; }

	[JsonPropertyName("faces")]
	public IEnumerable<FontFace> Faces { get; set; }
}
