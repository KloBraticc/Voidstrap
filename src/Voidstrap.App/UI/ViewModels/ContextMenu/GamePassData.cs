namespace Voidstrap.UI.ViewModels.ContextMenu;

internal class GamePassData
{
	public long GamePassId { get; set; }

	public long IconAssetId { get; set; }

	public string Name { get; set; } = string.Empty;

	public string Description { get; set; } = string.Empty;

	public bool IsForSale { get; set; }

	public int? Price { get; set; }

	public GamePassCreator Creator { get; set; } = new GamePassCreator();

	public string IconUrl { get; set; } = string.Empty;

	public string DisplayPrice { get; set; } = string.Empty;

	public string CreatorName => Creator?.Name ?? "Unknown";
}
