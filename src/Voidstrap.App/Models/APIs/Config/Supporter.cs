using System;
using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs.Config;

public class Supporter
{
	[JsonPropertyName("imageAsset")]
	public string ImageAsset { get; set; } = null!;

	[JsonPropertyName("name")]
	public string Name { get; set; } = null!;

	public string Image
	{
		get
		{
			if (string.IsNullOrEmpty(ImageAsset))
			{
				return "pack://application:,,,/Voidstrap.png";
			}
			if (!ImageAsset.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				return "https://raw.githubusercontent.com/bloxstraplabs/config/main/assets/" + ImageAsset;
			}
			return ImageAsset;
		}
	}
}
