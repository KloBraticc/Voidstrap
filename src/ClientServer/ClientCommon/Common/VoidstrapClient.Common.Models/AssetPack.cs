using System.Collections.Generic;
using System.Text.Json.Serialization;
using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.Common.Models;

public class AssetPack
{
	[JsonIgnore]
	public string Folder { get; set; } = "";

	[JsonIgnore]
	public string FolderName { get; set; } = "";

	public string? Name { get; set; }

	[JsonIgnore]
	public string DisplayName
	{
		get
		{
			if (!string.IsNullOrEmpty(Name))
			{
				return Name;
			}
			return FolderName;
		}
	}

	public string Description { get; set; } = "";

	public string Author { get; set; } = "";

	public string Version { get; set; } = "";

	public List<string> Clients { get; set; } = new List<string> { "*" };

	[JsonIgnore]
	public bool Disabled { get; set; }

	[JsonIgnore]
	public AssetPackApi Api { get; set; }

	public override string ToString()
	{
		string text = DisplayName;
		if (Disabled)
		{
			text += " (Disabled)";
		}
		return text;
	}
}
