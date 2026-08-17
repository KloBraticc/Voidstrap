namespace Voidstrap.Integrations;

public readonly struct AssetTypeInfo
{
	public string Type { get; init; }

	public string Category { get; init; }

	public string Extension { get; init; }

	public bool IsImage { get; init; }

	public bool IsMesh { get; init; }

	public static AssetTypeInfo Unknown => new AssetTypeInfo
	{
		Type = "Other",
		Category = "Other",
		Extension = ".bin",
		IsImage = false,
		IsMesh = false
	};
}
